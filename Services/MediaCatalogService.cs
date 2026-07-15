using AutoVJ.Models;

namespace AutoVJ.Services;

public sealed class MediaCatalogService(
    AppConfig config,
    FfmpegService ffmpeg,
    FingerprintService fingerprints,
    DatabaseService database,
    ILogger<MediaCatalogService> logger)
{
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);

    // 登録済み曲をWeb UI表示用の軽量な情報へ変換します。
    public async Task<List<TrackSummary>> GetSummariesAsync()
    {
        var tracks = await database.GetTracksAsync();
        return tracks.Select(track => new TrackSummary(
            track.Id,
            track.Name,
            Path.GetFileName(track.VideoPath),
            track.DurationSeconds)).ToList();
    }

    // 各MP4の音声トラックから指紋を生成し、動画単位でDBを再構築します。
    public async Task<List<TrackSummary>> RebuildAsync(CancellationToken cancellationToken = default)
    {
        await _rebuildLock.WaitAsync(cancellationToken);
        try
        {
            var videoDirectory = Path.GetFullPath(config.Media.MainVideoDir);
            Directory.CreateDirectory(videoDirectory);

            var videoFiles = Directory.EnumerateFiles(videoDirectory, "*.mp4")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var videoDurations = await ReadDurationsAsync(videoFiles, cancellationToken);
            var records = new List<TrackRecord>();

            foreach (var videoPath in videoFiles)
            {
                logger.LogInformation("動画の音声指紋を生成しています: {Video}", Path.GetFileName(videoPath));
                var samples = await ffmpeg.DecodeAudioAsync(videoPath, cancellationToken: cancellationToken);
                var fingerprint = fingerprints.Create(samples);
                records.Add(new TrackRecord(
                    0,
                    Path.GetFileNameWithoutExtension(videoPath),
                    videoPath,
                    videoDurations[videoPath],
                    fingerprint));
            }

            await database.ReplaceTracksAsync(records);
            return await GetSummariesAsync();
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    // 複数メディアの再生時間を順番に読み取り、パスごとの辞書を作ります。
    private async Task<Dictionary<string, double>> ReadDurationsAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var durations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            durations[path] = await ffmpeg.GetDurationAsync(path, cancellationToken);
        }
        return durations;
    }
}
