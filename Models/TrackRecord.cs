namespace AutoVJ.Models;

public sealed record TrackRecord(
    long Id,
    string Name,
    string AudioPath,
    string VideoPath,
    double DurationSeconds,
    byte[] Fingerprint);

public sealed record TrackSummary(
    long Id,
    string Name,
    string AudioFile,
    string VideoFile,
    double DurationSeconds);

public sealed record MatchResult(
    TrackRecord? Track,
    double Confidence,
    double PositionSeconds);
