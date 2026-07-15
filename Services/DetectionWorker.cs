using AutoVJ.Models;

namespace AutoVJ.Services;

public sealed class DetectionWorker(
    AppConfig config,
    AudioCaptureService capture,
    FingerprintService fingerprints,
    DatabaseService database,
    RuntimeState state,
    ILogger<DetectionWorker> logger) : BackgroundService
{
    // 一定間隔で最新音声窓を登録指紋と照合し、共有状態を更新します。
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.Detection.IntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!capture.IsRunning)
            {
                continue;
            }

            try
            {
                var samples = capture.GetLatestWindow();
                if (samples.Length == 0)
                {
                    continue;
                }
                var query = fingerprints.Create(samples);
                var tracks = await database.GetTracksAsync();
                var result = fingerprints.Match(query, tracks);
                state.SetMatch(result);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "リアルタイム曲検出に失敗しました。");
                state.SetCapture(capture.IsRunning, $"検出エラー: {exception.Message}");
            }
        }
    }
}
