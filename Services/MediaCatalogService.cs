using AutoVJ.Models;
using Microsoft.Extensions.Logging;

namespace AutoVJ.Services;

public sealed class MediaCatalogService(
    AppConfig config,
    FfmpegService ffmpeg,
    FingerprintService fingerprints,
    DatabaseService database,
    ILogger<MediaCatalogService> logger)
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    // main-videosを走査し、新規・更新動画だけを解析してDBへ順次反映します。
    public async Task<CatalogScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            var videoDirectory = Path.GetFullPath(config.Media.MainVideoDir);
            Directory.CreateDirectory(videoDirectory);
            var videoFiles = Directory.EnumerateFiles(videoDirectory, "*.mp4")
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var existingEntries = (await database.GetCatalogEntriesAsync(cancellationToken))
                .ToDictionary(entry => entry.VideoPath, StringComparer.OrdinalIgnoreCase);

            var added = 0;
            var updated = 0;
            var unchanged = 0;
            var failed = 0;

            foreach (var videoPath in videoFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = new FileInfo(videoPath);
                var modifiedUtc = file.LastWriteTimeUtc.ToString("O");
                existingEntries.TryGetValue(videoPath, out var existing);
                if (existing is not null
                    && existing.FileSize == file.Length
                    && string.Equals(existing.FileModifiedUtc, modifiedUtc, StringComparison.Ordinal))
                {
                    unchanged++;
                    logger.LogInformation("変更なし: {Video}", file.Name);
                    continue;
                }

                try
                {
                    logger.LogInformation("解析中: {Video}", file.Name);
                    var duration = await ffmpeg.GetDurationAsync(videoPath, cancellationToken);
                    var samples = await ffmpeg.DecodeAudioAsync(videoPath, cancellationToken: cancellationToken);
                    var fingerprint = fingerprints.Create(samples);
                    if (fingerprint.Length == 0)
                    {
                        throw new InvalidOperationException("音声指紋を生成できませんでした。");
                    }

                    var stablePath = existing?.VideoPath ?? videoPath;
                    await database.UpsertTrackAsync(new TrackRecord(
                        existing?.Id ?? 0,
                        Path.GetFileNameWithoutExtension(videoPath),
                        stablePath,
                        duration,
                        fingerprint,
                        file.Length,
                        modifiedUtc), cancellationToken);
                    if (existing is null) added++; else updated++;
                    logger.LogInformation("登録完了: {Video}", file.Name);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed++;
                    logger.LogError(exception, "解析失敗。既存レコードは維持します: {Video}", file.Name);
                }
            }

            var retainedPaths = videoFiles
                .Select(path => existingEntries.TryGetValue(path, out var entry) ? entry.VideoPath : path)
                .ToArray();
            var removed = await database.DeleteTracksNotInAsync(retainedPaths, cancellationToken);
            return new CatalogScanResult(videoFiles.Count, added, updated, unchanged, removed, failed);
        }
        finally
        {
            _scanLock.Release();
        }
    }
}
