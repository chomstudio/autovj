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
    private long? _tentativeTrackId;
    private int _tentativeCount;

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
                var inputDecibels = CalculateDecibels(samples);
                if (inputDecibels < config.Detection.MinimumInputDecibels)
                {
                    ResetTentativeMatch();
                    state.SetMatch(new MatchResult(null, 0, 0), TimeSpan.FromSeconds(config.Playback.DetectionLostTimeoutSeconds));
                    continue;
                }
                var query = fingerprints.Create(samples);
                var tracks = await database.GetTracksAsync();
                var result = fingerprints.Match(query, tracks, config.Detection.TentativeConfidenceThreshold);
                if (result.Track is null)
                {
                    ResetTentativeMatch();
                    state.SetMatch(result, TimeSpan.FromSeconds(config.Playback.DetectionLostTimeoutSeconds));
                    continue;
                }

                if (result.Confidence >= config.Detection.ConfidenceThreshold)
                {
                    ResetTentativeMatch();
                    state.SetMatch(result, TimeSpan.FromSeconds(config.Playback.DetectionLostTimeoutSeconds));
                    continue;
                }

                if (_tentativeTrackId == result.Track.Id)
                {
                    _tentativeCount++;
                }
                else
                {
                    _tentativeTrackId = result.Track.Id;
                    _tentativeCount = 1;
                }

                if (_tentativeCount >= config.Detection.TentativeConfirmationCount)
                {
                    ResetTentativeMatch();
                    state.SetMatch(result, TimeSpan.FromSeconds(config.Playback.DetectionLostTimeoutSeconds));
                }
                else
                {
                    state.SetMatch(new MatchResult(null, result.Confidence, 0), TimeSpan.FromSeconds(config.Playback.DetectionLostTimeoutSeconds));
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "リアルタイム曲検出に失敗しました。");
                state.SetCapture(capture.IsRunning, $"検出エラー: {exception.Message}");
            }
        }
    }

    // PCM窓全体のRMSをdBFSへ変換し、無音ゲート判定に使用します。
    private static double CalculateDecibels(float[] samples)
    {
        var meanSquare = samples.Sum(sample => sample * sample) / samples.Length;
        return Math.Max(-120, 20 * Math.Log10(Math.Max(Math.Sqrt(meanSquare), 0.000001)));
    }

    // 弱い候補の連続確認状態を破棄します。
    private void ResetTentativeMatch()
    {
        _tentativeTrackId = null;
        _tentativeCount = 0;
    }
}
