using AutoVJ.Models;

namespace AutoVJ.Services;

public interface IAudioFingerprintDetector
{
    string Method { get; }
    int Version { get; }

    // PCMから検出器固有の指紋バイト列を生成します。
    byte[] Create(float[] samples);

    // 1曲の参照指紋を照合し、最良の信頼度・位置・テンポ倍率を返します。
    DetectorMatch Match(byte[] query, TrackRecord track, IReadOnlyList<double> tempoRatios);
}

public sealed record DetectorMatch(double Confidence, double PositionSeconds, double TempoRatio);
