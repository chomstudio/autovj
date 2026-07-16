using System.Globalization;
using AutoVJ.Models;

namespace AutoVJ.Services;

public static class ConfigService
{
    private static readonly object SaveLock = new();

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
            case "server.auto_open_browser": config.Server.AutoOpenBrowser = bool.Parse(value); break;
            case "media.input_audio_dir": config.Media.InputAudioDir = value; break;
            case "media.main_video_dir": config.Media.MainVideoDir = value; break;
            case "media.material_video_dir": config.Media.MaterialVideoDir = value; break;
            case "media.common_video_file": config.Media.CommonVideoFile = value; break;
            case "audio.preferred_input": config.Audio.PreferredInput = value; break;
            case "audio.sample_rate": config.Audio.SampleRate = int.Parse(value, culture); break;
            case "audio.detection_window_seconds": config.Audio.DetectionWindowSeconds = int.Parse(value, culture); break;
            case "detection.interval_seconds": config.Detection.IntervalSeconds = int.Parse(value, culture); break;
            case "detection.confidence_threshold": config.Detection.ConfidenceThreshold = double.Parse(value, numberStyle, culture); break;
            case "detection.tentative_confidence_threshold": config.Detection.TentativeConfidenceThreshold = double.Parse(value, numberStyle, culture); break;
            case "detection.tentative_confirmation_count": config.Detection.TentativeConfirmationCount = int.Parse(value, culture); break;
            case "detection.minimum_input_decibels": config.Detection.MinimumInputDecibels = double.Parse(value, numberStyle, culture); break;
            case "detection.tempo_ratios": config.Detection.TempoRatios = ParseDoubleList(value); break;
            case "detection.playback_rate_tolerance": config.Detection.PlaybackRateTolerance = double.Parse(value, numberStyle, culture); break;
            case "playback.detection_lost_timeout_seconds": config.Playback.DetectionLostTimeoutSeconds = int.Parse(value, culture); break;
            case "playback.resync_tolerance_seconds": config.Playback.ResyncToleranceSeconds = double.Parse(value, numberStyle, culture); break;
            case "playback.randomize_common_start": config.Playback.RandomizeCommonStart = bool.Parse(value); break;
            case "transition.enabled": config.Transition.Enabled = bool.Parse(value); break;
            case "transition.duration_ms": config.Transition.DurationMilliseconds = int.Parse(value, culture); break;
            case "transition.blend_modes_enabled": config.Transition.BlendModesEnabled = bool.Parse(value); break;
            case "transition.randomize_blend_mode": config.Transition.RandomizeBlendMode = bool.Parse(value); break;
            case "transition.blend_modes": config.Transition.BlendModes = ParseStringList(value); break;
            case "glitch.enabled": config.Glitch.Enabled = bool.Parse(value); break;
            case "glitch.confidence_threshold": config.Glitch.ConfidenceThreshold = double.Parse(value, numberStyle, culture); break;
            case "glitch.files": config.Glitch.Files = ParseStringList(value); break;
            case "storage.database_path": config.Storage.DatabasePath = value; break;
            case "ffmpeg.path": config.Ffmpeg.Path = value; break;
            case "ffmpeg.ffprobe_path": config.Ffmpeg.FfprobePath = value; break;
        }
    }

    // 詳細設定画面で許可された項目だけを検証し、実行中設定とYAMLへ保存します。
    public static void SaveUiSettings(string path, AppConfig config, UiSettingsRequest settings)
    {
        if (settings.DetectionLostTimeoutSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(settings.DetectionLostTimeoutSeconds), "未検出待機時間は1〜120秒で指定してください。");
        if (settings.ResyncToleranceSeconds is < 0.1 or > 30) throw new ArgumentOutOfRangeException(nameof(settings.ResyncToleranceSeconds), "再同期許容差は0.1〜30秒で指定してください。");
        if (settings.TransitionDurationMilliseconds is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(settings.TransitionDurationMilliseconds), "切り替え時間は0〜10000ミリ秒で指定してください。");
        if (settings.GlitchConfidenceThreshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(settings.GlitchConfidenceThreshold), "グリッチしきい値は0〜1で指定してください。");

        var allowedModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "normal", "multiply", "screen", "overlay", "darken", "lighten",
            "color-dodge", "color-burn", "hard-light", "soft-light", "difference", "exclusion"
        };
        var modes = settings.BlendModes.Select(mode => mode.Trim().ToLowerInvariant())
            .Where(allowedModes.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (modes.Count == 0) throw new ArgumentException("ブレンドモードを1つ以上選択してください。", nameof(settings.BlendModes));

        lock (SaveLock)
        {
            config.Playback.DetectionLostTimeoutSeconds = settings.DetectionLostTimeoutSeconds;
            config.Playback.ResyncToleranceSeconds = settings.ResyncToleranceSeconds;
            config.Transition.Enabled = settings.TransitionEnabled;
            config.Transition.DurationMilliseconds = settings.TransitionDurationMilliseconds;
            config.Transition.BlendModesEnabled = settings.BlendModesEnabled;
            config.Transition.RandomizeBlendMode = settings.RandomizeBlendMode;
            config.Transition.BlendModes = modes;
            config.Glitch.ConfidenceThreshold = settings.GlitchConfidenceThreshold;

            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["playback.detection_lost_timeout_seconds"] = settings.DetectionLostTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                ["playback.resync_tolerance_seconds"] = settings.ResyncToleranceSeconds.ToString(CultureInfo.InvariantCulture),
                ["transition.enabled"] = settings.TransitionEnabled.ToString().ToLowerInvariant(),
                ["transition.duration_ms"] = settings.TransitionDurationMilliseconds.ToString(CultureInfo.InvariantCulture),
                ["transition.blend_modes_enabled"] = settings.BlendModesEnabled.ToString().ToLowerInvariant(),
                ["transition.randomize_blend_mode"] = settings.RandomizeBlendMode.ToString().ToLowerInvariant(),
                ["transition.blend_modes"] = $"[{string.Join(", ", modes.Select(mode => $"\"{mode}\""))}]",
                ["glitch.confidence_threshold"] = settings.GlitchConfidenceThreshold.ToString(CultureInfo.InvariantCulture)
            };
            RewriteKnownValues(path, replacements);
        }
    }

    // YAMLの構造と未対象項目を維持したまま、指定済みキーの値だけを書き換えます。
    private static void RewriteKnownValues(string path, IReadOnlyDictionary<string, string> replacements)
    {
        var lines = File.ReadAllLines(path);
        var section = string.Empty;
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (!char.IsWhiteSpace(lines[index].FirstOrDefault()) && trimmed.EndsWith(':'))
            {
                section = trimmed[..^1];
                continue;
            }
            var separator = trimmed.IndexOf(':');
            if (separator < 0) continue;
            var key = trimmed[..separator].Trim();
            if (replacements.TryGetValue($"{section}.{key}", out var value))
            {
                var indent = lines[index][..(lines[index].Length - lines[index].TrimStart().Length)];
                lines[index] = $"{indent}{key}: {value}";
            }
        }

        var temporaryPath = $"{path}.tmp";
        File.WriteAllLines(temporaryPath, lines);
        File.Move(temporaryPath, path, true);
    }

    // YAMLのインライン配列を文字列一覧へ変換します。
    private static List<string> ParseStringList(string value)
    {
        return value.Trim().TrimStart('[').TrimEnd(']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim('"', '\''))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    // YAMLのインライン配列をテンポ倍率一覧へ変換します。
    private static List<double> ParseDoubleList(string value)
    {
        return value.Trim().TrimStart('[').TrimEnd(']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => double.Parse(item, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToList();
    }
}
