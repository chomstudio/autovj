using System.Globalization;
using AutoVJ.Models;

namespace AutoVJ.Services;

public static class ConfigService
{
    // MVPで使用する既知のYAMLキーだけを読み込み、外部依存なしで設定を構築します。
    public static AppConfig Load(string path)
    {
        var config = new AppConfig();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("設定ファイルが見つかりません。", path);
        }

        string section = string.Empty;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Split('#', 2)[0].TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!char.IsWhiteSpace(rawLine[0]) && line.EndsWith(':'))
            {
                section = line[..^1].Trim();
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            Apply(config, section, key, value);
        }

        return config;
    }

    // YAMLから得た値を型付き設定の対応プロパティへ割り当てます。
    private static void Apply(AppConfig config, string section, string key, string value)
    {
        var numberStyle = NumberStyles.Float;
        var culture = CultureInfo.InvariantCulture;

        switch ($"{section}.{key}")
        {
            case "server.host": config.Server.Host = value; break;
            case "server.port": config.Server.Port = int.Parse(value, culture); break;
            case "media.input_audio_dir": config.Media.InputAudioDir = value; break;
            case "media.main_video_dir": config.Media.MainVideoDir = value; break;
            case "media.pairing_duration_tolerance_seconds": config.Media.PairingDurationToleranceSeconds = double.Parse(value, numberStyle, culture); break;
            case "audio.preferred_input": config.Audio.PreferredInput = value; break;
            case "audio.sample_rate": config.Audio.SampleRate = int.Parse(value, culture); break;
            case "audio.detection_window_seconds": config.Audio.DetectionWindowSeconds = int.Parse(value, culture); break;
            case "detection.interval_seconds": config.Detection.IntervalSeconds = int.Parse(value, culture); break;
            case "detection.confidence_threshold": config.Detection.ConfidenceThreshold = double.Parse(value, numberStyle, culture); break;
            case "storage.database_path": config.Storage.DatabasePath = value; break;
            case "ffmpeg.path": config.Ffmpeg.Path = value; break;
            case "ffmpeg.ffprobe_path": config.Ffmpeg.FfprobePath = value; break;
        }
    }
}
