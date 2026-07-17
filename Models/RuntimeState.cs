using AutoVJ.Services;

namespace AutoVJ.Models;

public sealed class RuntimeState
{
    private readonly object _sync = new();
    private readonly AppConfig _config;
    private readonly Random _random = new();
    private RuntimeSnapshot _snapshot;
    private DateTimeOffset? _lastSuccessfulMatch;

    // 設定済みデバイス名を含む初期状態を作成します。
    public RuntimeState(AppConfig config)
    {
        _config = config;
        var sourceId = $"{config.Audio.PreferredInputType}:{config.Audio.PreferredInput}";
        _snapshot = new RuntimeSnapshot(
            false, "停止中", sourceId, config.Audio.PreferredInput, config.Audio.PreferredInputType,
            0, -60, null, 0, 0, null, 0, null, null, 1.0,
            FingerprintService.LegacyMethod, FingerprintService.LegacyVersion,
            0, "normal", null, _random.NextDouble());
    }

    // 複数スレッドから参照される状態を安全に複製して返します。
    public RuntimeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    // 音声入力の稼働状態とメッセージを更新します。
    public void SetCapture(bool running, string message, string? sourceId = null, string? deviceName = null, string? sourceType = null)
    {
        lock (_sync)
        {
            var fallbackChanged = !running && _snapshot.TrackId is not null;
            if (!running)
            {
                _lastSuccessfulMatch = null;
            }
            _snapshot = _snapshot with
            {
                CaptureRunning = running,
                Message = message,
                SelectedInputSourceId = sourceId ?? _snapshot.SelectedInputSourceId,
                SelectedInputDevice = deviceName ?? _snapshot.SelectedInputDevice,
                SelectedInputType = sourceType ?? _snapshot.SelectedInputType,
                InputLevel = running ? _snapshot.InputLevel : 0,
                InputDecibels = running ? _snapshot.InputDecibels : -60,
                TrackId = running ? _snapshot.TrackId : null,
                TrackName = running ? _snapshot.TrackName : null,
                Confidence = running ? _snapshot.Confidence : 0,
                PositionSeconds = running ? _snapshot.PositionSeconds : 0,
                ReferenceBpm = running ? _snapshot.ReferenceBpm : null,
                InputBpm = running ? _snapshot.InputBpm : null,
                TempoRatio = running ? _snapshot.TempoRatio : 1.0,
                TransitionRevision = fallbackChanged ? _snapshot.TransitionRevision + 1 : _snapshot.TransitionRevision,
                TransitionBlendMode = fallbackChanged ? ChooseBlendMode() : _snapshot.TransitionBlendMode,
                GlitchFileIndex = running ? _snapshot.GlitchFileIndex : null,
                CommonStartFraction = fallbackChanged ? _random.NextDouble() : _snapshot.CommonStartFraction
            };
        }
    }

    // 入力音声の平滑化済みレベルをWeb UI向け状態へ反映します。
    public void SetInputLevel(double level, double decibels)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with { InputLevel = level, InputDecibels = decibels };
        }
    }

    // 選択したデバイス名を録音停止中にもWeb UIへ反映します。
    public void SetSelectedDevice(string sourceId, string deviceName, string sourceType)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                SelectedInputSourceId = sourceId,
                SelectedInputDevice = deviceName,
                SelectedInputType = sourceType
            };
        }
    }

    // 保存直後の倍率制限とグリッチしきい値を現在の共有状態へ反映します。
    public void ApplySettings()
    {
        lock (_sync)
        {
            var showGlitch = _snapshot.CaptureRunning
                && _snapshot.TrackId is not null
                && _snapshot.Confidence < _config.Glitch.ConfidenceThreshold;
            _snapshot = _snapshot with
            {
                TempoRatio = Math.Clamp(_snapshot.TempoRatio, _config.Playback.MinimumRate, _config.Playback.MaximumRate),
                GlitchFileIndex = showGlitch
                    ? _snapshot.GlitchFileIndex ?? ChooseGlitchFile()
                    : null
            };
        }
    }

    // 最新の照合結果を反映し、未検出が続いた場合だけ汎用動画状態へ戻します。
    public void SetMatch(MatchResult result, TimeSpan lostTimeout)
    {
        lock (_sync)
        {
            if (result.Track is not null)
            {
                var sourceChanged = _snapshot.TrackId != result.Track.Id;
                _lastSuccessfulMatch = DateTimeOffset.UtcNow;
                var normalizedReferenceBpm = NormalizeBpm(result.ReferenceBpm);
                var normalizedInputBpm = NormalizeBpm(result.InputBpm);
                _snapshot = _snapshot with
                {
                    TrackId = result.Track.Id,
                    TrackName = result.Track.Name,
                    Confidence = result.Confidence,
                    PositionSeconds = result.PositionSeconds,
                    Message = $"{result.Track.Name} を検出",
                    MatchRevision = _snapshot.MatchRevision + 1,
                    ReferenceBpm = normalizedReferenceBpm,
                    InputBpm = normalizedInputBpm,
                    TempoRatio = Math.Clamp(result.TempoRatio, _config.Playback.MinimumRate, _config.Playback.MaximumRate),
                    FingerprintMethod = result.FingerprintMethod,
                    FingerprintVersion = result.FingerprintVersion,
                    TransitionRevision = sourceChanged ? _snapshot.TransitionRevision + 1 : _snapshot.TransitionRevision,
                    TransitionBlendMode = sourceChanged ? ChooseBlendMode() : _snapshot.TransitionBlendMode,
                    GlitchFileIndex = result.Confidence < _config.Glitch.ConfidenceThreshold
                        ? _snapshot.GlitchFileIndex ?? ChooseGlitchFile()
                        : null
                };
                return;
            }

            var timedOut = _lastSuccessfulMatch is null
                || DateTimeOffset.UtcNow - _lastSuccessfulMatch.Value >= lostTimeout;
            var fallbackChanged = timedOut && _snapshot.TrackId is not null;
            var glitchFileIndex = !timedOut && result.Confidence < _config.Glitch.ConfidenceThreshold
                ? _snapshot.GlitchFileIndex ?? ChooseGlitchFile()
                : null;
            _snapshot = _snapshot with
            {
                Confidence = result.Confidence,
                TrackId = timedOut ? null : _snapshot.TrackId,
                TrackName = timedOut ? null : _snapshot.TrackName,
                PositionSeconds = timedOut ? 0 : _snapshot.PositionSeconds,
                ReferenceBpm = timedOut ? null : _snapshot.ReferenceBpm,
                InputBpm = timedOut ? null : _snapshot.InputBpm,
                TempoRatio = timedOut ? 1.0 : _snapshot.TempoRatio,
                Message = timedOut ? "曲を探索中・汎用動画を再生" : "現在の曲を継続確認中",
                TransitionRevision = fallbackChanged ? _snapshot.TransitionRevision + 1 : _snapshot.TransitionRevision,
                TransitionBlendMode = fallbackChanged ? ChooseBlendMode() : _snapshot.TransitionBlendMode,
                GlitchFileIndex = glitchFileIndex,
                CommonStartFraction = fallbackChanged ? _random.NextDouble() : _snapshot.CommonStartFraction
            };
        }
    }

    // BPMを倍テン・半テンとして補正し、ユーザー指定範囲へ収めます。
    private double? NormalizeBpm(double? bpm)
    {
        if (bpm is not double value || value <= 0) return bpm;
        while (value < _config.Playback.MinimumBpm) value *= 2;
        while (value >= _config.Playback.MaximumBpm) value /= 2;
        return Math.Round(value, 2);
    }

    // 設定済み候補から全再生ページで共有する切り替え効果を選びます。
    private string ChooseBlendMode()
    {
        if (!_config.Transition.BlendModesEnabled || _config.Transition.BlendModes.Count == 0) return "normal";
        return _config.Transition.RandomizeBlendMode
            ? _config.Transition.BlendModes[_random.Next(_config.Transition.BlendModes.Count)]
            : _config.Transition.BlendModes[0];
    }

    // 全再生ページで同じ素材を重ねるため、グリッチ素材番号をサーバー側で選びます。
    private int? ChooseGlitchFile()
    {
        return _config.Glitch.Enabled && _config.Glitch.Files.Count > 0
            ? _random.Next(_config.Glitch.Files.Count)
            : null;
    }
}

public sealed record RuntimeSnapshot(
    bool CaptureRunning,
    string Message,
    string SelectedInputSourceId,
    string SelectedInputDevice,
    string SelectedInputType,
    double InputLevel,
    double InputDecibels,
    long? TrackId,
    double Confidence,
    double PositionSeconds,
    string? TrackName,
    long MatchRevision,
    double? ReferenceBpm,
    double? InputBpm,
    double TempoRatio,
    string FingerprintMethod,
    int FingerprintVersion,
    long TransitionRevision,
    string TransitionBlendMode,
    int? GlitchFileIndex,
    double CommonStartFraction);
