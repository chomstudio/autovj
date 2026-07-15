using AutoVJ.Models;

namespace AutoVJ.Services;

public sealed class SelfTestService(
    AppConfig config,
    FfmpegService ffmpeg,
    FingerprintService fingerprints,
    DatabaseService database,
    RuntimeState runtimeState)
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

        var revisionTrack = tracks[0];
        runtimeState.SetCapture(true, "自己診断中");
        runtimeState.SetMatch(new MatchResult(revisionTrack, 0.8, 20), TimeSpan.FromSeconds(10));
        var firstRevision = runtimeState.GetSnapshot();
        runtimeState.SetMatch(new MatchResult(null, 0.2, 0), TimeSpan.FromSeconds(10));
        var staleRevision = runtimeState.GetSnapshot();
        runtimeState.SetMatch(new MatchResult(revisionTrack, 0.8, 22), TimeSpan.FromSeconds(10));
        var nextRevision = runtimeState.GetSnapshot();
        var revisionPassed = staleRevision.TrackId == revisionTrack.Id
            && staleRevision.MatchRevision == firstRevision.MatchRevision
            && nextRevision.MatchRevision == firstRevision.MatchRevision + 1;
        allPassed &= revisionPassed;
        Console.WriteLine($"自己診断: {(revisionPassed ? "成功" : "失敗")} / 未検出中は検出番号を維持し、新しい位置検出時だけ更新");
        runtimeState.SetCapture(false, "停止中");

        return allPassed;
    }

    // 複数の検出窓長と再生位置を比較し、短縮後も同じ動画を検出できるか測定します。
    public async Task<bool> RunBenchmarkAsync(CancellationToken cancellationToken = default)
    {
        var tracks = await database.GetTracksAsync();
        var inputDirectory = Path.GetFullPath(config.Media.InputAudioDir);
        var inputFiles = Directory.Exists(inputDirectory)
            ? Directory.EnumerateFiles(inputDirectory)
                .Where(path => new[] { ".mp3", ".wav" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        if (tracks.Count == 0 || inputFiles.Count == 0)
        {
            Console.Error.WriteLine("検出ベンチマーク: 登録動画または確認用音源がありません。");
            return false;
        }

        var windowLengths = new[] { 2, 3, 4, 6, 8 };
        var fourSecondPassed = true;
        foreach (var inputPath in inputFiles)
        {
            var duration = await ffmpeg.GetDurationAsync(inputPath, cancellationToken);
            var baselineSamples = await ffmpeg.DecodeAudioAsync(inputPath, 30, 8, cancellationToken);
            var baseline = fingerprints.Match(fingerprints.Create(baselineSamples), tracks, 0);
            if (baseline.Track is null)
            {
                Console.Error.WriteLine($"検出ベンチマーク: {Path.GetFileName(inputPath)} の基準動画を決定できません。");
                fourSecondPassed = false;
                continue;
            }

            var starts = new[] { 10.0, duration * 0.33, duration * 0.66, Math.Max(0, duration - 20) }
                .Select(value => Math.Min(value, Math.Max(0, duration - 9)))
                .Distinct()
                .ToArray();
            foreach (var windowLength in windowLengths)
            {
                var scores = new List<double>();
                var strongPassed = 0;
                var tentativePassed = 0;
                foreach (var start in starts)
                {
                    var samples = await ffmpeg.DecodeAudioAsync(inputPath, start, windowLength, cancellationToken);
                    var result = fingerprints.Match(fingerprints.Create(samples), tracks, 0);
                    var correctTrack = result.Track?.Id == baseline.Track.Id;
                    if (correctTrack && result.Confidence >= config.Detection.ConfidenceThreshold)
                    {
                        strongPassed++;
                    }
                    if (correctTrack && result.Confidence >= config.Detection.TentativeConfidenceThreshold)
                    {
                        tentativePassed++;
                    }
                    scores.Add(result.Confidence);
                }

                if (windowLength == 4 && tentativePassed != starts.Length)
                {
                    fourSecondPassed = false;
                }
                Console.WriteLine(
                    $"検出ベンチマーク: 入力={Path.GetFileName(inputPath)} / 窓={windowLength}秒 / 即時={strongPassed}/{starts.Length} / 確認込み={tentativePassed}/{starts.Length} / 最小={scores.Min():P1} / 平均={scores.Average():P1}");
            }
        }

        var silentSamples = new float[config.Audio.SampleRate * 4];
        var silentResult = fingerprints.Match(fingerprints.Create(silentSamples), tracks, 0);
        Console.WriteLine($"検出ベンチマーク: デジタル無音の最大一致度={silentResult.Confidence:P1}");
        return fourSecondPassed;
    }
}
