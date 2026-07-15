namespace AutoVJ.Models;

public sealed class RuntimeState
{
    private readonly object _sync = new();
    private RuntimeSnapshot _snapshot = new(false, "停止中", null, 0, 0, null);

    // 複数スレッドから参照される状態を安全に複製して返します。
    public RuntimeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    // 音声入力の稼働状態とメッセージを更新します。
    public void SetCapture(bool running, string message)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with { CaptureRunning = running, Message = message };
        }
    }

    // 最新の照合結果をWeb UI向け状態へ反映します。
    public void SetMatch(MatchResult result)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                TrackId = result.Track?.Id,
                TrackName = result.Track?.Name,
                Confidence = result.Confidence,
                PositionSeconds = result.PositionSeconds,
                Message = result.Track is null ? "曲を探索中" : $"{result.Track.Name} を検出"
            };
        }
    }
}

public sealed record RuntimeSnapshot(
    bool CaptureRunning,
    string Message,
    long? TrackId,
    double Confidence,
    double PositionSeconds,
    string? TrackName);
