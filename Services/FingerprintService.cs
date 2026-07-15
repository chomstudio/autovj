using System.Numerics;
using AutoVJ.Models;

namespace AutoVJ.Services;

public sealed class FingerprintService(AppConfig config)
{
    private const int WindowSize = 2048;
    private const int HopSize = 1024;
    private const int BandsPerFrame = 4;
    private static readonly (double Low, double High)[] FrequencyBands =
    {
        (80, 250), (250, 600), (600, 1400), (1400, 3200)
    };

    // PCMサンプルを短時間周波数ピークの列へ変換します。
    public byte[] Create(float[] samples)
    {
        if (samples.Length < WindowSize)
        {
            return [];
        }

        var frameCount = 1 + (samples.Length - WindowSize) / HopSize;
        var fingerprint = new byte[frameCount * BandsPerFrame];
        var spectrum = new Complex[WindowSize];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * HopSize;
            for (var i = 0; i < WindowSize; i++)
            {
                var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1));
                spectrum[i] = new Complex(samples[offset + i] * window, 0);
            }

            Transform(spectrum);
            for (var band = 0; band < FrequencyBands.Length; band++)
            {
                fingerprint[frame * BandsPerFrame + band] = FindPeak(spectrum, FrequencyBands[band]);
            }
        }

        return fingerprint;
    }

    // 入力指紋を全登録曲へ照合し、最も信頼度の高い曲と位置を返します。
    public MatchResult Match(byte[] query, IReadOnlyList<TrackRecord> tracks, double? confidenceThreshold = null)
    {
        TrackRecord? bestTrack = null;
        var bestConfidence = 0.0;
        var bestFrame = 0;

        foreach (var track in tracks)
        {
            var (confidence, frame) = FindBestOffset(query, track.Fingerprint);
            if (confidence > bestConfidence)
            {
                bestTrack = track;
                bestConfidence = confidence;
                bestFrame = frame;
            }
        }

        var position = (bestFrame + query.Length / BandsPerFrame) * HopSize / (double)config.Audio.SampleRate;
        var threshold = confidenceThreshold ?? config.Detection.ConfidenceThreshold;
        return bestConfidence >= threshold
            ? new MatchResult(bestTrack, bestConfidence, position)
            : new MatchResult(null, bestConfidence, 0);
    }

    // 参照指紋上を走査し、帯域ピークが最もよく一致する開始位置を求めます。
    private static (double Confidence, int Frame) FindBestOffset(byte[] query, byte[] reference)
    {
        var queryFrames = query.Length / BandsPerFrame;
        var referenceFrames = reference.Length / BandsPerFrame;
        if (queryFrames == 0 || referenceFrames < queryFrames)
        {
            return (0, 0);
        }

        var bestScore = 0.0;
        var bestFrame = 0;
        for (var start = 0; start <= referenceFrames - queryFrames; start++)
        {
            var matches = 0;
            for (var frame = 0; frame < queryFrames; frame++)
            {
                for (var band = 0; band < BandsPerFrame; band++)
                {
                    var queryValue = query[frame * BandsPerFrame + band];
                    var referenceValue = reference[(start + frame) * BandsPerFrame + band];
                    if (Math.Abs(queryValue - referenceValue) <= 1)
                    {
                        matches++;
                    }
                }
            }

            var score = matches / (double)(queryFrames * BandsPerFrame);
            if (score > bestScore)
            {
                bestScore = score;
                bestFrame = start;
            }
        }

        return (bestScore, bestFrame);
    }

    // 指定周波数帯で最大エネルギーとなるビンを粗く量子化します。
    private byte FindPeak(Complex[] spectrum, (double Low, double High) band)
    {
        var lowBin = Math.Max(1, (int)(band.Low * WindowSize / config.Audio.SampleRate));
        var highBin = Math.Min(WindowSize / 2 - 1, (int)(band.High * WindowSize / config.Audio.SampleRate));
        var peakBin = lowBin;
        var peakPower = 0.0;

        for (var bin = lowBin; bin <= highBin; bin++)
        {
            var power = spectrum[bin].Magnitude;
            if (power > peakPower)
            {
                peakPower = power;
                peakBin = bin;
            }
        }

        return (byte)Math.Clamp((peakBin - lowBin) / 2, 0, byte.MaxValue);
    }

    // Cooley-Tukey法によるインプレースFFTを実行します。
    private static void Transform(Complex[] values)
    {
        var length = values.Length;
        var reversed = 0;
        for (var i = 1; i < length; i++)
        {
            var bit = length >> 1;
            while ((reversed & bit) != 0)
            {
                reversed ^= bit;
                bit >>= 1;
            }
            reversed ^= bit;
            if (i < reversed)
            {
                (values[i], values[reversed]) = (values[reversed], values[i]);
            }
        }

        for (var size = 2; size <= length; size <<= 1)
        {
            var angle = -2 * Math.PI / size;
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < length; start += size)
            {
                var factor = Complex.One;
                for (var i = 0; i < size / 2; i++)
                {
                    var even = values[start + i];
                    var odd = values[start + i + size / 2] * factor;
                    values[start + i] = even + odd;
                    values[start + i + size / 2] = even - odd;
                    factor *= step;
                }
            }
        }
    }
}
