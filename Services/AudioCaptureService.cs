using AutoVJ.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AutoVJ.Services;

public sealed class AudioCaptureService(AppConfig config, RuntimeState state, ILogger<AudioCaptureService> logger) : IDisposable
{
    private readonly object _sampleLock = new();
    private readonly Queue<float> _samples = new();
    private WasapiCapture? _capture;
    private double _resampleAccumulator;
    private double _smoothedLevel;
    private string _selectedSourceId = $"{config.Audio.PreferredInputType}:{config.Audio.PreferredInput}";

    public bool IsRunning => _capture is not null;

    // 録音入力と再生出力のループバック候補を、重複しないID付きで列挙します。
    public IReadOnlyList<AudioSourceInfo> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var captureSources = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new AudioSourceInfo(
                $"capture:{device.ID}", device.FriendlyName, "capture", $"録音入力: {device.FriendlyName}"));
        var loopbackSources = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => new AudioSourceInfo(
                $"loopback:{device.ID}", device.FriendlyName, "loopback", $"ループバック: {device.FriendlyName}"));
        return captureSources.Concat(loopbackSources)
            .OrderBy(source => source.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // 指定した録音入力またはWASAPIループバックから音声監視を開始します。
    public void Start(string? requestedSource = null)
    {
        var source = ResolveSource(requestedSource ?? _selectedSourceId);
        if (_capture is not null && _selectedSourceId.Equals(source.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (_capture is not null)
        {
            Stop();
        }

        using var enumerator = new MMDeviceEnumerator();
        var endpointId = source.Id[(source.Id.IndexOf(':') + 1)..];
        var device = enumerator.GetDevice(endpointId);

        lock (_sampleLock)
        {
            _samples.Clear();
            _resampleAccumulator = 0;
            _smoothedLevel = 0;
        }

        _selectedSourceId = source.Id;
        _capture = source.Type == "loopback"
            ? new WasapiLoopbackCapture(device)
            : new WasapiCapture(device);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        var modeLabel = source.Type == "loopback" ? "ループバック" : "録音入力";
        state.SetCapture(true, $"{modeLabel}: {device.FriendlyName} を監視中", source.Id, device.FriendlyName, source.Type);
        logger.LogInformation("音声入力を開始しました: {Mode} / {Device} / {Format}", modeLabel, device.FriendlyName, _capture.WaveFormat);
    }

    // WASAPI録音を停止し、使用中のデバイスを解放します。
    public void Stop()
    {
        var capture = _capture;
        if (capture is null)
        {
            state.SetCapture(false, "停止中");
            return;
        }
        capture.StopRecording();
        CleanupCapture(capture);
        state.SetCapture(false, "停止中");
    }

    // 選択デバイスを変更し、監視中であれば新しいデバイスで即座に再開します。
    public void SelectDevice(string sourceId)
    {
        var source = ResolveSource(sourceId);
        var wasRunning = IsRunning;
        if (wasRunning)
        {
            Stop();
        }
        _selectedSourceId = source.Id;
        state.SetSelectedDevice(source.Id, source.Name, source.Type);
        if (wasRunning)
        {
            Start(source.Id);
        }
    }

    // 検出窓に必要な最新PCMサンプルをコピーして返します。
    public float[] GetLatestWindow()
    {
        lock (_sampleLock)
        {
            var required = config.Audio.SampleRate * config.Audio.DetectionWindowSeconds;
            return _samples.Count >= required ? _samples.TakeLast(required).ToArray() : [];
        }
    }

    // WASAPIバッファをモノラル化・簡易リサンプリングしてリングバッファへ加えます。
    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }

        var format = capture.WaveFormat;
        var frameSize = format.BlockAlign;
        var sampleSize = format.BitsPerSample / 8;
        var frameCount = args.BytesRecorded / frameSize;
        var added = new List<float>(Math.Max(1, frameCount * config.Audio.SampleRate / format.SampleRate));

        for (var frame = 0; frame < frameCount; frame++)
        {
            var mono = 0f;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frame * frameSize + channel * sampleSize;
                mono += ReadSample(args.Buffer, offset, format.BitsPerSample, format.Encoding);
            }
            mono /= format.Channels;

            _resampleAccumulator += config.Audio.SampleRate;
            if (_resampleAccumulator >= format.SampleRate)
            {
                added.Add(mono);
                _resampleAccumulator -= format.SampleRate;
            }
        }

        lock (_sampleLock)
        {
            foreach (var sample in added)
            {
                _samples.Enqueue(sample);
            }
            var maximum = config.Audio.SampleRate * (config.Audio.DetectionWindowSeconds + 2);
            while (_samples.Count > maximum)
            {
                _samples.Dequeue();
            }
        }

        if (added.Count > 0)
        {
            var rms = Math.Sqrt(added.Sum(sample => sample * sample) / added.Count);
            var decibels = Math.Max(-60, 20 * Math.Log10(Math.Max(rms, 0.000001)));
            var normalized = Math.Clamp((decibels + 60) / 60, 0, 1);
            _smoothedLevel = _smoothedLevel * 0.72 + normalized * 0.28;
            state.SetInputLevel(_smoothedLevel, decibels);
        }
    }

    // APIのIDを優先し、旧設定との互換用に種類と表示名でも入力元を解決します。
    private AudioSourceInfo ResolveSource(string requestedSource)
    {
        var sources = GetDevices();
        var configuredType = config.Audio.PreferredInputType;
        var requestedName = requestedSource;
        if (requestedSource.StartsWith("capture:", StringComparison.OrdinalIgnoreCase)
            || requestedSource.StartsWith("loopback:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = requestedSource.IndexOf(':');
            configuredType = requestedSource[..separator];
            requestedName = requestedSource[(separator + 1)..];
        }
        var resolved = sources.FirstOrDefault(source => source.Id.Equals(requestedSource, StringComparison.OrdinalIgnoreCase))
            ?? sources.FirstOrDefault(source =>
                source.Type.Equals(configuredType, StringComparison.OrdinalIgnoreCase)
                && source.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
            ?? sources.FirstOrDefault(source =>
                source.Type.Equals(configuredType, StringComparison.OrdinalIgnoreCase)
                && source.Name.Contains(requestedName, StringComparison.OrdinalIgnoreCase));
        return resolved ?? throw new InvalidOperationException($"音声入力元 '{requestedSource}' が見つかりません。");
    }

    // 録音形式に応じて1サンプルを-1から1のfloatへ変換します。
    private static float ReadSample(byte[] buffer, int offset, int bitsPerSample, WaveFormatEncoding encoding)
    {
        if (bitsPerSample == 32 && encoding != WaveFormatEncoding.Pcm)
        {
            return BitConverter.ToSingle(buffer, offset);
        }
        if (bitsPerSample == 32)
        {
            return BitConverter.ToInt32(buffer, offset) / 2147483648f;
        }
        if (bitsPerSample == 24)
        {
            var value = buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16;
            if ((value & 0x800000) != 0) value |= unchecked((int)0xff000000);
            return value / 8388608f;
        }
        if (bitsPerSample == 16)
        {
            return BitConverter.ToInt16(buffer, offset) / 32768f;
        }
        throw new NotSupportedException($"未対応の録音形式です: {bitsPerSample}bit / {encoding}");
    }

    // 録音停止イベントを状態へ反映し、異常停止をログへ残します。
    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null)
        {
            logger.LogError(args.Exception, "音声入力が異常停止しました。");
            state.SetCapture(false, $"音声入力エラー: {args.Exception.Message}");
        }
    }

    // イベントを解除して録音オブジェクトを重複なく破棄します。
    private void CleanupCapture(WasapiCapture capture)
    {
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        capture.Dispose();
        if (ReferenceEquals(_capture, capture))
        {
            _capture = null;
        }
    }

    // アプリ終了時にも録音デバイスが残らないよう停止します。
    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
