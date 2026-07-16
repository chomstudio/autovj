using AutoVJ.Services;

namespace AutoVJ.Models;

public sealed class RuntimeState
{
    private readonly object _sync = new();
    private RuntimeSnapshot _snapshot;
    private DateTimeOffset? _lastSuccessfulMatch;

    // 設定済みデバイス名を含む初期状態を作成します。
    public RuntimeState(AppConfig config)
    {
        _snapshot = new RuntimeSnapshot(false, "停止中", config.Audio.PreferredInput, 0, -60, null, 0, 0, null, 0, null, null, 1.0, FingerprintService.LegacyMethod, FingerprintService.LegacyVersion);
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
    public void SetCapture(bool running, string message, string? deviceName = null)
    {
        lock (_sync)
        {
            if (!running)
            {
                _lastSuccessfulMatch = null;
            }
            _snapshot = _snapshot with
            {
                CaptureRunning = running,
                Message = message,
                SelectedInputDevice = deviceName ?? _snapshot.SelectedInputDevice,
                InputLevel = running ? _snapshot.InputLevel : 0,
                InputDecibels = running ? _snapshot.InputDecibels : -60,
                TrackId = running ? _snapshot.TrackId : null,
                TrackName = running ? _snapshot.TrackName : null,
                Confidence = running ? _snapshot.Confidence : 0,
                PositionSeconds = running ? _snapshot.PositionSeconds : 0,
                ReferenceBpm = running ? _snapshot.ReferenceBpm : null,
                InputBpm = running ? _snapshot.InputBpm : null,
                TempoRatio = running ? _snapshot.TempoRatio : 1.0
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
    public void SetSelectedDevice(string deviceName)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with { SelectedInputDevice = deviceName };
        }
    }

    // 最新の照合結果を反映し、未検出が続いた場合だけ汎用動画状態へ戻します。
    public void SetMatch(MatchResult result, TimeSpan lostTimeout)
    {
        lock (_sync)
        {
            if (result.Track is not null)
            {
                _lastSuccessfulMatch = DateTimeOffset.UtcNow;
                _snapshot = _snapshot with
                {
                    TrackId = result.Track.Id,
                    TrackName = result.Track.Name,
                    Confidence = result.Confidence,
                    PositionSeconds = result.PositionSeconds,
                    Message = $"{result.Track.Name} を検出",
                    MatchRevision = _snapshot.MatchRevision + 1,
                    ReferenceBpm = result.ReferenceBpm,
                    InputBpm = result.InputBpm,
                    TempoRatio = result.TempoRatio,
                    FingerprintMethod = result.FingerprintMethod,
                    FingerprintVersion = result.FingerprintVersion
                };
                return;
            }

            var timedOut = _lastSuccessfulMatch is null
                || DateTimeOffset.UtcNow - _lastSuccessfulMatch.Value >= lostTimeout;
            _snapshot = _snapshot with
            {
                Confidence = result.Confidence,
                TrackId = timedOut ? null : _snapshot.TrackId,
                TrackName = timedOut ? null : _snapshot.TrackName,
                PositionSeconds = timedOut ? 0 : _snapshot.PositionSeconds,
                ReferenceBpm = timedOut ? null : _snapshot.ReferenceBpm,
                InputBpm = timedOut ? null : _snapshot.InputBpm,
                TempoRatio = timedOut ? 1.0 : _snapshot.TempoRatio,
                Message = timedOut ? "曲を探索中・汎用動画を再生" : "現在の曲を継続確認中"
            };
        }
    }
}

public sealed record RuntimeSnapshot(
    bool CaptureRunning,
    string Message,
    string SelectedInputDevice,
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
    int FingerprintVersion);
