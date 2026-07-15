namespace AutoVJ.Models;

public sealed record TrackRecord(
    long Id,
    string Name,
    string VideoPath,
    double DurationSeconds,
    byte[] Fingerprint,
    long FileSize = 0,
    string FileModifiedUtc = "");

public sealed record CatalogEntry(
    long Id,
    string VideoPath,
    long FileSize,
    string FileModifiedUtc);

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
    double DurationSeconds);

public sealed record MatchResult(
    TrackRecord? Track,
    double Confidence,
    double PositionSeconds);
