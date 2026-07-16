namespace AutoVJ.Models;

public sealed record TrackRecord(
    long Id,
    string Name,
    string VideoPath,
    double DurationSeconds,
    byte[] Fingerprint,
    long FileSize = 0,
    string FileModifiedUtc = "",
    string FingerprintMethod = "band-peaks",
    int FingerprintVersion = 1,
    byte[]? TempoFingerprint = null,
    double? Bpm = null,
    double BpmConfidence = 0,
    string BpmSource = "unknown");

public sealed record CatalogEntry(
    long Id,
    string VideoPath,
    long FileSize,
    string FileModifiedUtc,
    string FingerprintMethod,
    int FingerprintVersion);

public sealed record CatalogScanResult(
    int Scanned,
    int Added,
    int Updated,
    int Unchanged,
    int Removed,
    int Failed);

public sealed record TrackSummary(
    long Id,
    string Name,
    string VideoFile,
    double DurationSeconds,
    double? Bpm,
    double BpmConfidence,
    string BpmSource,
    string FingerprintMethod,
    int FingerprintVersion);

public sealed record MatchResult(
    TrackRecord? Track,
    double Confidence,
    double PositionSeconds,
    double TempoRatio = 1.0,
    double? ReferenceBpm = null,
    double? InputBpm = null,
    string FingerprintMethod = "band-peaks",
    int FingerprintVersion = 1);

public sealed record BpmEstimate(double? Bpm, double Confidence, string Source);
