using System.Diagnostics;
using System.Globalization;
using AutoVJ.Models;

namespace AutoVJ.Services;

public sealed class FfmpegService(AppConfig config)
{
    // ffprobeを使ってメディア全体の再生時間を秒単位で取得します。
    public async Task<double> GetDurationAsync(string path, CancellationToken cancellationToken = default)
    {
        var arguments = new[]
        {
            "-v", "error", "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", path
        };
        var result = await RunTextAsync(config.Ffmpeg.FfprobePath, arguments, cancellationToken);
        return double.Parse(result.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // FFmpegで音声をモノラルfloat PCMへ変換し、指紋処理用サンプルを返します。
    public async Task<float[]> DecodeAudioAsync(
        string path,
        double? startSeconds = null,
        double? durationSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(config.Ffmpeg.Path);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        if (startSeconds is not null)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(startSeconds.Value.ToString(CultureInfo.InvariantCulture));
        }
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(path);
        if (durationSeconds is not null)
        {
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(durationSeconds.Value.ToString(CultureInfo.InvariantCulture));
        }
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add(config.Audio.SampleRate.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("f32le");
        startInfo.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpegの音声変換に失敗しました: {error.Trim()}");
        }

        var bytes = buffer.ToArray();
        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }

    // 指定位置の音声へテンポ・EQ条件を適用し、ベンチマーク用PCMを生成します。
    public async Task<float[]> DecodeBenchmarkQueryAsync(
        string path,
        double startSeconds,
        double outputDurationSeconds,
        double tempoRatio,
        string condition,
        CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(config.Ffmpeg.Path);
        foreach (var argument in new[] { "-v", "error", "-ss", startSeconds.ToString(CultureInfo.InvariantCulture), "-i", path })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var filters = new List<string>();
        if (condition == "speed-and-pitch")
        {
            filters.Add($"asetrate={config.Audio.SampleRate.ToString(CultureInfo.InvariantCulture)}*{tempoRatio.ToString(CultureInfo.InvariantCulture)}");
            filters.Add($"aresample={config.Audio.SampleRate.ToString(CultureInfo.InvariantCulture)}");
        }
        else
        {
            filters.Add($"atempo={tempoRatio.ToString(CultureInfo.InvariantCulture)}");
        }
        if (condition == "highpass") filters.Add("highpass=f=1200");
        if (condition == "lowpass") filters.Add("lowpass=f=500");
        if (condition == "filter-release") filters.Add($"highpass=f=1200:enable='lt(t,{(outputDurationSeconds / 2).ToString(CultureInfo.InvariantCulture)})'");

        foreach (var argument in new[]
        {
            "-vn", "-af", string.Join(',', filters), "-t", outputDurationSeconds.ToString(CultureInfo.InvariantCulture),
            "-ac", "1", "-ar", config.Audio.SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpegのベンチマーク音源生成に失敗しました: {error.Trim()}");
        var bytes = buffer.ToArray();
        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }

    // 外部コマンドを実行し、標準出力を文字列として取得します。
    private static async Task<string> RunTextAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(executable);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} の実行に失敗しました: {error.Trim()}");
        }
        return output;
    }

    // 標準入出力をリダイレクトした安全なプロセス起動設定を作ります。
    private static ProcessStartInfo CreateStartInfo(string executable)
    {
        return new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }
}
