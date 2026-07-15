using AutoVJ.Models;

namespace AutoVJ.Services;

public sealed class SelfTestService(
    AppConfig config,
    FfmpegService ffmpeg,
    FingerprintService fingerprints,
    DatabaseService database)
{
    // input-audioの各音源を未知入力として、動画由来の指紋へ照合できるか検証します。
    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        var tracks = await database.GetTracksAsync();
        if (tracks.Count == 0)
        {
            Console.Error.WriteLine("自己診断: 登録曲がありません。");
            return false;
        }

        var inputDirectory = Path.GetFullPath(config.Media.InputAudioDir);
        var inputFiles = Directory.Exists(inputDirectory)
            ? Directory.EnumerateFiles(inputDirectory)
                .Where(path => new[] { ".mp3", ".wav" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        if (inputFiles.Count == 0)
        {
            Console.Error.WriteLine("自己診断: input-audioに確認用音源がありません。");
            return false;
        }

        var allPassed = true;
        foreach (var inputPath in inputFiles)
        {
            var duration = await ffmpeg.GetDurationAsync(inputPath, cancellationToken);
            var start = Math.Min(30, Math.Max(0, duration / 3));
            var samples = await ffmpeg.DecodeAudioAsync(
                inputPath,
                start,
                config.Audio.DetectionWindowSeconds,
                cancellationToken);
            var result = fingerprints.Match(fingerprints.Create(samples), tracks);
            var passed = result.Track is not null;
            allPassed &= passed;
            Console.WriteLine(
                $"自己診断: {(passed ? "成功" : "失敗")} / 入力={Path.GetFileName(inputPath)} / 検出動画={result.Track?.Name ?? "なし"} / 信頼度={result.Confidence:P1} / 位置={result.PositionSeconds:F1}秒");
        }

        return allPassed;
    }
}
