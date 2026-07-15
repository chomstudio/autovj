using AutoVJ.Models;

namespace AutoVJ.Services;

public sealed class SelfTestService(
    AppConfig config,
    FfmpegService ffmpeg,
    FingerprintService fingerprints,
    DatabaseService database)
{
    // 各登録音源の途中8秒を未知入力として照合し、正しい曲へ戻るか検証します。
    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        var tracks = await database.GetTracksAsync();
        if (tracks.Count == 0)
        {
            Console.Error.WriteLine("自己診断: 登録曲がありません。");
            return false;
        }

        var allPassed = true;
        foreach (var expected in tracks)
        {
            var start = Math.Min(30, Math.Max(0, expected.DurationSeconds / 3));
            var samples = await ffmpeg.DecodeAudioAsync(
                expected.AudioPath,
                start,
                config.Audio.DetectionWindowSeconds,
                cancellationToken);
            var result = fingerprints.Match(fingerprints.Create(samples), tracks);
            var passed = result.Track?.Id == expected.Id;
            allPassed &= passed;
            Console.WriteLine(
                $"自己診断: {(passed ? "成功" : "失敗")} / 期待={expected.Name} / 検出={result.Track?.Name ?? "なし"} / 信頼度={result.Confidence:P1} / 位置={result.PositionSeconds:F1}秒");
        }

        return allPassed;
    }
}
