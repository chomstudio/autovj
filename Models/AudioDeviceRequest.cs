namespace AutoVJ.Models;

public sealed record AudioDeviceRequest(string? DeviceName, string? SourceId = null);

public sealed record AudioSourceInfo(string Id, string Name, string Type, string Label);
