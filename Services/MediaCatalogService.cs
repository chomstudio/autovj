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
            Path.GetFileName(track.AudioPath),
            Path.GetFileName(track.VideoPath),
            track.DurationSeconds)).ToList();
    }

    // 音源と動画を再生時間で対応付けし、指紋を生成してDBを再構築します。
    public async Task<List<TrackSummary>> RebuildAsync(CancellationToken cancellationToken = default)
    {
        await _rebuildLock.WaitAsync(cancellationToken);
        try
        {
            var audioDirectory = Path.GetFullPath(config.Media.InputAudioDir);
            var videoDirectory = Path.GetFullPath(config.Media.MainVideoDir);
            Directory.CreateDirectory(audioDirectory);
            Directory.CreateDirectory(videoDirectory);

            var audioFiles = Directory.EnumerateFiles(audioDirectory)
                .Where(path => new[] { ".mp3", ".wav" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var videoFiles = Directory.EnumerateFiles(videoDirectory, "*.mp4")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var audioDurations = await ReadDurationsAsync(audioFiles, cancellationToken);
            var videoDurations = await ReadDurationsAsync(videoFiles, cancellationToken);
            var remainingVideos = new HashSet<string>(videoFiles, StringComparer.OrdinalIgnoreCase);
            var records = new List<TrackRecord>();

            foreach (var audioPath in audioFiles)
            {
                var videoPath = remainingVideos
                    .OrderBy(path => Math.Abs(videoDurations[path] - audioDurations[audioPath]))
                    .FirstOrDefault();
                if (videoPath is null)
                {
                    logger.LogWarning("対応する動画がないため {Audio} を登録しません。", audioPath);
                    continue;
                }

                var difference = Math.Abs(videoDurations[videoPath] - audioDurations[audioPath]);
                if (difference > config.Media.PairingDurationToleranceSeconds)
                {
                    logger.LogWarning("再生時間差が {Difference:F2} 秒あるため {Audio} を登録しません。", difference, audioPath);
                    continue;
                }

                logger.LogInformation("指紋を生成しています: {Audio}", Path.GetFileName(audioPath));
                var samples = await ffmpeg.DecodeAudioAsync(audioPath, cancellationToken: cancellationToken);
                var fingerprint = fingerprints.Create(samples);
                records.Add(new TrackRecord(
                    0,
                    Path.GetFileNameWithoutExtension(audioPath),
                    audioPath,
                    videoPath,
                    audioDurations[audioPath],
                    fingerprint));
                remainingVideos.Remove(videoPath);
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
