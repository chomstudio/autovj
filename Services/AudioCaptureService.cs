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

    public bool IsRunning => _capture is not null;

    // Windowsで現在有効な録音エンドポイント名を列挙します。
    public IReadOnlyList<string> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => device.FriendlyName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // 設定名に一致するWASAPI録音デバイスから入力監視を開始します。
    public void Start()
    {
        if (_capture is not null)
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(candidate => candidate.FriendlyName.Equals(config.Audio.PreferredInput, StringComparison.OrdinalIgnoreCase))
            ?? enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(candidate => candidate.FriendlyName.Contains(config.Audio.PreferredInput, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            throw new InvalidOperationException($"録音デバイス '{config.Audio.PreferredInput}' が見つかりません。");
        }

        lock (_sampleLock)
        {
            _samples.Clear();
            _resampleAccumulator = 0;
        }

        _capture = new WasapiCapture(device);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        state.SetCapture(true, $"{device.FriendlyName} を監視中");
        logger.LogInformation("音声入力を開始しました: {Device} / {Format}", device.FriendlyName, _capture.WaveFormat);
    }

    // WASAPI録音を停止し、使用中のデバイスを解放します。
    public void Stop()
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }
        capture.StopRecording();
        CleanupCapture(capture);
        state.SetCapture(false, "停止中");
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
