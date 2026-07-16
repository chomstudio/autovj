using System.Diagnostics;
using System.Globalization;
using System.Text;
using AutoVJ.Models;

namespace AutoVJ.Services;

public sealed class TempoBenchmarkService(
    AppConfig config,
    FfmpegService ffmpeg,
    FingerprintService fingerprints,
    DatabaseService database)
{
    // M0.5のキーロック倍率・EQ条件を実音源から生成し、再現可能なCSVへ記録します。
    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        var tracks = (await database.GetTracksAsync(cancellationToken))
            .Where(track => track.TempoFingerprint is { Length: > 0 } && File.Exists(track.VideoPath))
            .ToList();
        if (tracks.Count == 0)
        {
            Console.Error.WriteLine("M0.5ベンチマーク: M0.5指紋を持つ登録動画がありません。先にCatalog scanを実行してください。");
            return false;
        }

        var cases = BuildCases(config.Detection.TempoRatios);
        var outputDirectory = Path.GetFullPath("benchmark-results");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, $"m05-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        await using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
        await writer.WriteLineAsync("method,version,track_id,track,ratio,condition,window_seconds,start_seconds,detected_track,confidence,correct,false_positive,miss,position_error_seconds,confirmation_seconds,processing_ms,reference_bpm,input_bpm");

        var total = 0;
        var correct = 0;
        var falsePositives = 0;
        var misses = 0;
        var normalSpeedPassed = true;
        foreach (var track in tracks)
        {
            foreach (var benchmarkCase in cases)
            {
                foreach (var start in GetStarts(track.DurationSeconds, benchmarkCase.WindowSeconds, benchmarkCase.MultiplePositions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var samples = await ffmpeg.DecodeBenchmarkQueryAsync(
                        track.VideoPath, start, benchmarkCase.WindowSeconds, benchmarkCase.Ratio,
                        benchmarkCase.Condition, cancellationToken);
                    var stopwatch = Stopwatch.StartNew();
                    var result = fingerprints.Match(samples, tracks, 0, [benchmarkCase.Ratio]);
                    stopwatch.Stop();

                    var isCorrect = result.Track?.Id == track.Id;
                    var accepted = result.Confidence >= config.Detection.TentativeConfidenceThreshold;
                    var falsePositive = accepted && !isCorrect;
                    var miss = !accepted;
                    var expectedPosition = start + benchmarkCase.WindowSeconds * benchmarkCase.Ratio;
                    var positionError = isCorrect ? Math.Abs(result.PositionSeconds - expectedPosition) : double.NaN;
                    var confirmation = result.Confidence >= config.Detection.ConfidenceThreshold
                        ? config.Detection.IntervalSeconds
                        : accepted ? config.Detection.IntervalSeconds * config.Detection.TentativeConfirmationCount : double.NaN;

                    total++;
                    if (isCorrect && accepted) correct++;
                    if (falsePositive) falsePositives++;
                    if (miss) misses++;
                    if (benchmarkCase.Ratio == 1.0 && benchmarkCase.Condition == "clean" && (!isCorrect || !accepted)) normalSpeedPassed = false;

                    await writer.WriteLineAsync(string.Join(',', new[]
                    {
                        FingerprintService.TempoMethod,
                        FingerprintService.TempoVersion.ToString(CultureInfo.InvariantCulture),
                        track.Id.ToString(CultureInfo.InvariantCulture), Csv(track.Name),
                        benchmarkCase.Ratio.ToString("F2", CultureInfo.InvariantCulture), benchmarkCase.Condition,
                        benchmarkCase.WindowSeconds.ToString(CultureInfo.InvariantCulture), start.ToString("F3", CultureInfo.InvariantCulture),
                        Csv(result.Track?.Name ?? string.Empty), result.Confidence.ToString("F6", CultureInfo.InvariantCulture),
                        (isCorrect && accepted ? 1 : 0).ToString(CultureInfo.InvariantCulture),
                        (falsePositive ? 1 : 0).ToString(CultureInfo.InvariantCulture), (miss ? 1 : 0).ToString(CultureInfo.InvariantCulture),
                        double.IsNaN(positionError) ? string.Empty : positionError.ToString("F3", CultureInfo.InvariantCulture),
                        double.IsNaN(confirmation) ? string.Empty : confirmation.ToString("F3", CultureInfo.InvariantCulture),
                        stopwatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                        result.ReferenceBpm?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty,
                        result.InputBpm?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty
                    }));
                    Console.WriteLine($"M0.5ベンチマーク: {track.Name} / {benchmarkCase.Ratio:F2} / {benchmarkCase.Condition} / {benchmarkCase.WindowSeconds}秒 / {(isCorrect && accepted ? "成功" : falsePositive ? "誤検出" : "未検出")} / {result.Confidence:P1}");
                }
            }
        }

        Console.WriteLine($"M0.5ベンチマーク完了: 正解={correct}/{total} / 誤検出={falsePositives} / 未検出={misses} / CSV={outputPath}");
        Console.WriteLine($"通常速度回帰: {(normalSpeedPassed ? "成功" : "失敗")}");
        return normalSpeedPassed;
    }

    // 必須倍率は全窓・複数位置、EQと速度ピッチ比較は代表条件へ絞って構築します。
    private static List<BenchmarkCase> BuildCases(IEnumerable<double> configuredRatios)
    {
        var cases = configuredRatios.Distinct().OrderBy(value => value)
            .SelectMany(ratio => new[] { 4, 6, 8 }.Select(window => new BenchmarkCase(ratio, "clean", window, true)))
            .ToList();
        foreach (var ratio in new[] { 1.0, 1.15 })
        {
            foreach (var condition in new[] { "highpass", "lowpass", "filter-release" })
            {
                cases.Add(new BenchmarkCase(ratio, condition, 4, false));
                cases.Add(new BenchmarkCase(ratio, condition, 8, false));
            }
        }
        cases.Add(new BenchmarkCase(0.93, "speed-and-pitch", 4, false));
        cases.Add(new BenchmarkCase(1.15, "speed-and-pitch", 4, false));
        return cases;
    }

    // 曲の前半・中央・後半から、安全に収まる3つの開始位置を返します。
    private static double[] GetStarts(double duration, int windowSeconds, bool multiplePositions)
    {
        var maximum = Math.Max(0, duration - windowSeconds * 1.2 - 1);
        var starts = multiplePositions ? new[] { duration * 0.2, duration * 0.5, duration * 0.75 } : new[] { duration * 0.5 };
        return starts.Select(value => Math.Clamp(value, 0, maximum)).Distinct().ToArray();
    }

    // CSV内のカンマや引用符を安全にエスケープします。
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private sealed record BenchmarkCase(double Ratio, string Condition, int WindowSeconds, bool MultiplePositions);
}
