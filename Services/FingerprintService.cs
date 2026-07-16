using System.Numerics;
using AutoVJ.Models;

namespace AutoVJ.Services;

public sealed class FingerprintService
{
    public const string LegacyMethod = "band-peaks";
    public const int LegacyVersion = 1;
    public const string TempoMethod = "spectral-landmarks";
    public const int TempoVersion = 2;

    private readonly AppConfig _config;
    private readonly IAudioFingerprintDetector _legacy;
    private readonly IAudioFingerprintDetector _tempo;

    // 旧方式とM0.5方式を共通インターフェースで保持します。
    public FingerprintService(AppConfig config)
    {
        _config = config;
        _legacy = new BandPeakDetector(config);
        _tempo = new TempoLandmarkDetector(config);
    }

    // 旧DBとの互換用に4帯域ピーク指紋を生成します。
    public byte[] Create(float[] samples) => _legacy.Create(samples);

    // キーロック時の時間軸伸縮を照合できる局所ピーク指紋を生成します。
    public byte[] CreateTempoFingerprint(float[] samples) => _tempo.Create(samples);

    // 旧ベンチマーク用に4帯域指紋だけを通常速度で照合します。
    public MatchResult Match(byte[] query, IReadOnlyList<TrackRecord> tracks, double? confidenceThreshold = null)
    {
        return MatchWithDetector(_legacy, query, tracks, [1.0], confidenceThreshold);
    }

    // 新方式を優先し、未再解析レコードだけ旧方式へフォールバックします。
    public MatchResult Match(
        float[] samples,
        IReadOnlyList<TrackRecord> tracks,
        double? confidenceThreshold = null,
        IReadOnlyList<double>? tempoRatios = null)
    {
        var inputBpmEstimate = EstimateBpm(samples);
        var tempoTracks = tracks.Where(track => track.TempoFingerprint is { Length: > 0 }).ToList();
        var best = tempoTracks.Count > 0
            ? MatchWithDetector(_tempo, _tempo.Create(samples), tempoTracks,
                NormalizeRatios(tempoRatios ?? _config.Detection.TempoRatios), 0, inputBpmEstimate)
            : new MatchResult(null, 0, 0);

        var legacyTracks = tracks.Where(track => track.TempoFingerprint is not { Length: > 0 }).ToList();
        if (legacyTracks.Count > 0)
        {
            var legacy = MatchWithDetector(_legacy, _legacy.Create(samples), legacyTracks, [1.0], 0);
            if (legacy.Confidence > best.Confidence)
            {
                best = legacy;
            }
        }

        var threshold = confidenceThreshold ?? _config.Detection.ConfidenceThreshold;
        return best.Confidence >= threshold ? best : best with { Track = null, PositionSeconds = 0 };
    }

    // BPM候補を全体のオンセット強度の自己相関から推定します。
    public BpmEstimate EstimateBpm(float[] samples)
    {
        const int envelopeHop = 256;
        if (samples.Length < _config.Audio.SampleRate * 4)
        {
            return new BpmEstimate(null, 0, "automatic");
        }

        var envelope = new double[samples.Length / envelopeHop];
        var previous = 0.0;
        for (var frame = 0; frame < envelope.Length; frame++)
        {
            var start = frame * envelopeHop;
            var end = Math.Min(samples.Length, start + envelopeHop);
            var energy = 0.0;
            for (var index = start; index < end; index++) energy += samples[index] * samples[index];
            energy = Math.Sqrt(energy / Math.Max(1, end - start));
            envelope[frame] = Math.Max(0, energy - previous);
            previous = energy;
        }

        var envelopeRate = _config.Audio.SampleRate / (double)envelopeHop;
        var minLag = Math.Max(1, (int)Math.Round(envelopeRate * 60 / 190));
        var maxLag = Math.Min(envelope.Length / 2, (int)Math.Round(envelopeRate * 60 / 70));
        var bestLag = 0;
        var best = 0.0;
        var mean = envelope.Average();
        var variance = envelope.Sum(value => (value - mean) * (value - mean));
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var correlation = 0.0;
            for (var index = lag; index < envelope.Length; index++)
            {
                correlation += (envelope[index] - mean) * (envelope[index - lag] - mean);
            }
            if (correlation > best)
            {
                best = correlation;
                bestLag = lag;
            }
        }

        if (bestLag == 0 || variance <= 0)
        {
            return new BpmEstimate(null, 0, "automatic");
        }
        var bpm = 60 * envelopeRate / bestLag;
        var confidence = Math.Clamp(best / variance, 0, 1);
        return new BpmEstimate(Math.Round(bpm, 2), confidence, "automatic");
    }

    // 指定検出器で全曲を比較し、最良候補を共通結果へ変換します。
    private static MatchResult MatchWithDetector(
        IAudioFingerprintDetector detector,
        byte[] query,
        IReadOnlyList<TrackRecord> tracks,
        IReadOnlyList<double> ratios,
        double? confidenceThreshold,
        BpmEstimate? inputBpmEstimate = null)
    {
        TrackRecord? bestTrack = null;
        var best = new DetectorMatch(0, 0, 1);
        foreach (var track in tracks)
        {
            var orderedRatios = OrderRatiosByBpm(ratios, track, inputBpmEstimate);
            var candidate = detector.Match(query, track, orderedRatios);
            if (candidate.Confidence > best.Confidence)
            {
                bestTrack = track;
                best = candidate;
            }
        }

        var referenceBpm = bestTrack?.Bpm;
        var inputBpm = referenceBpm * best.TempoRatio;
        var result = new MatchResult(bestTrack, best.Confidence, best.PositionSeconds, best.TempoRatio,
            referenceBpm, inputBpm, detector.Method, detector.Version);
        return confidenceThreshold is null || result.Confidence >= confidenceThreshold
            ? result
            : result with { Track = null, PositionSeconds = 0 };
    }

    // 入力BPMが安定している場合、その曲の基準BPMから近い倍率を先に試します。
    private static IReadOnlyList<double> OrderRatiosByBpm(
        IReadOnlyList<double> ratios,
        TrackRecord track,
        BpmEstimate? inputBpmEstimate)
    {
        if (inputBpmEstimate?.Bpm is not double inputBpm
            || inputBpmEstimate.Confidence < 0.08
            || track.Bpm is not double referenceBpm
            || referenceBpm <= 0)
        {
            return ratios;
        }

        var candidate = inputBpm / referenceBpm;
        while (candidate < 0.75) candidate *= 2;
        while (candidate > 1.35) candidate /= 2;
        return ratios.OrderBy(ratio => Math.Abs(ratio - candidate)).ToArray();
    }

    // 設定倍率を安全範囲へ丸め、通常速度を必ず含めます。
    private static double[] NormalizeRatios(IEnumerable<double> ratios)
    {
        return ratios.Append(1.0).Where(value => value is >= 0.75 and <= 1.35)
            .Distinct().OrderBy(value => Math.Abs(value - 1.0)).ToArray();
    }
}

internal sealed class BandPeakDetector(AppConfig config) : IAudioFingerprintDetector
{
    private const int BandsPerFrame = 4;
    private static readonly (double Low, double High)[] Bands =
    {
        (80, 250), (250, 600), (600, 1400), (1400, 3200)
    };
    public string Method => FingerprintService.LegacyMethod;
    public int Version => FingerprintService.LegacyVersion;

    // M0.4と同じ4帯域ピーク指紋を生成します。
    public byte[] Create(float[] samples) => SpectralPeakCodec.Create(samples, config.Audio.SampleRate, Bands, false);

    // M0.4指紋を通常速度で照合します。
    public DetectorMatch Match(byte[] query, TrackRecord track, IReadOnlyList<double> tempoRatios)
    {
        var match = SpectralPeakCodec.FindBest(query, track.Fingerprint, BandsPerFrame, [1.0]);
        var queryFrames = query.Length / BandsPerFrame;
        return new DetectorMatch(match.Confidence, (match.Frame + queryFrames) * SpectralPeakCodec.HopSize / (double)config.Audio.SampleRate, 1.0);
    }
}

internal sealed class TempoLandmarkDetector(AppConfig config) : IAudioFingerprintDetector
{
    private const int BandsPerFrame = 8;
    private static readonly (double Low, double High)[] Bands =
    {
        (70, 140), (140, 250), (250, 420), (420, 700),
        (700, 1100), (1100, 1700), (1700, 2500), (2500, 3600)
    };
    public string Method => FingerprintService.TempoMethod;
    public int Version => FingerprintService.TempoVersion;

    // 対数周波数寄りの8帯域から局所スペクトルピーク関係を保存します。
    public byte[] Create(float[] samples) => SpectralPeakCodec.Create(samples, config.Audio.SampleRate, Bands);

    // 時間軸倍率ごとに参照フレームを走査し、最良の曲位置を返します。
    public DetectorMatch Match(byte[] query, TrackRecord track, IReadOnlyList<double> tempoRatios)
    {
        if (track.TempoFingerprint is not { Length: > 0 } reference)
        {
            return new DetectorMatch(0, 0, 1);
        }
        var match = SpectralPeakCodec.FindBest(query, reference, BandsPerFrame, tempoRatios, 8);
        var queryFrames = query.Length / BandsPerFrame;
        return new DetectorMatch(match.Confidence, (match.Frame + queryFrames * match.Ratio) * SpectralPeakCodec.HopSize / (double)config.Audio.SampleRate, match.Ratio);
    }
}

internal static class SpectralPeakCodec
{
    public const int WindowSize = 2048;
    public const int HopSize = 1024;

    // 各フレームの帯域内最大ピークを量子化して連続バイト列へ変換します。
    public static byte[] Create(float[] samples, int sampleRate, IReadOnlyList<(double Low, double High)> bands, bool normalized = true)
    {
        if (samples.Length < WindowSize) return [];
        var frames = 1 + (samples.Length - WindowSize) / HopSize;
        var fingerprint = new byte[frames * bands.Count];
        var spectrum = new Complex[WindowSize];
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * HopSize;
            for (var index = 0; index < WindowSize; index++)
            {
                var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (WindowSize - 1));
                spectrum[index] = new Complex(samples[offset + index] * window, 0);
            }
            Transform(spectrum);
            for (var band = 0; band < bands.Count; band++)
            {
                fingerprint[frame * bands.Count + band] = FindPeak(spectrum, sampleRate, bands[band], normalized);
            }
        }
        return fingerprint;
    }

    // 登録時刻=入力時刻×倍率+開始位置として、全倍率と開始位置を比較します。
    public static (double Confidence, int Frame, double Ratio) FindBest(
        byte[] query, byte[] reference, int bandsPerFrame, IReadOnlyList<double> ratios, int tolerance = 1)
    {
        var queryFrames = query.Length / bandsPerFrame;
        var referenceFrames = reference.Length / bandsPerFrame;
        var bestScore = 0.0;
        var bestFrame = 0;
        var bestRatio = 1.0;
        if (queryFrames == 0) return (0, 0, 1);

        foreach (var ratio in ratios)
        {
            var span = (int)Math.Ceiling((queryFrames - 1) * ratio) + 1;
            if (referenceFrames < span) continue;
            for (var start = 0; start <= referenceFrames - span; start++)
            {
                var matches = 0;
                for (var frame = 0; frame < queryFrames; frame++)
                {
                    var referenceFrame = start + Math.Min(span - 1, (int)Math.Round(frame * ratio));
                    for (var band = 0; band < bandsPerFrame; band++)
                    {
                        if (Math.Abs(query[frame * bandsPerFrame + band] - reference[referenceFrame * bandsPerFrame + band]) <= tolerance)
                        {
                            matches++;
                        }
                    }
                }
                var score = matches / (double)(queryFrames * bandsPerFrame);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestFrame = start;
                    bestRatio = ratio;
                }
            }
        }
        return (bestScore, bestFrame, bestRatio);
    }

    // 指定帯域で最大のFFTビンを帯域幅に対して256段階へ正規化します。
    private static byte FindPeak(Complex[] spectrum, int sampleRate, (double Low, double High) band, bool normalized)
    {
        var low = Math.Max(1, (int)(band.Low * WindowSize / sampleRate));
        var high = Math.Min(WindowSize / 2 - 1, (int)(band.High * WindowSize / sampleRate));
        var peak = low;
        var power = 0.0;
        for (var bin = low; bin <= high; bin++)
        {
            if (spectrum[bin].Magnitude <= power) continue;
            power = spectrum[bin].Magnitude;
            peak = bin;
        }
        return normalized
            ? (byte)Math.Clamp((peak - low) * 255 / Math.Max(1, high - low), 0, 255)
            : (byte)Math.Clamp((peak - low) / 2, 0, 255);
    }

    // Cooley-Tukey法のインプレースFFTを実行します。
    private static void Transform(Complex[] values)
    {
        var reversed = 0;
        for (var index = 1; index < values.Length; index++)
        {
            var bit = values.Length >> 1;
            while ((reversed & bit) != 0) { reversed ^= bit; bit >>= 1; }
            reversed ^= bit;
            if (index < reversed) (values[index], values[reversed]) = (values[reversed], values[index]);
        }
        for (var size = 2; size <= values.Length; size <<= 1)
        {
            var step = new Complex(Math.Cos(-2 * Math.PI / size), Math.Sin(-2 * Math.PI / size));
            for (var start = 0; start < values.Length; start += size)
            {
                var factor = Complex.One;
                for (var index = 0; index < size / 2; index++)
                {
                    var even = values[start + index];
                    var odd = values[start + index + size / 2] * factor;
                    values[start + index] = even + odd;
                    values[start + index + size / 2] = even - odd;
                    factor *= step;
                }
            }
        }
    }
}
