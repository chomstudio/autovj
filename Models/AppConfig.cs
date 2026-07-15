namespace AutoVJ.Models;

public sealed class AppConfig
{
    public ServerConfig Server { get; } = new();
    public MediaConfig Media { get; } = new();
    public AudioConfig Audio { get; } = new();
    public DetectionConfig Detection { get; } = new();
    public StorageConfig Storage { get; } = new();
    public FfmpegConfig Ffmpeg { get; } = new();
}

public sealed class ServerConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5180;
}

public sealed class MediaConfig
{
    public string InputAudioDir { get; set; } = "input-audio";
    public string MainVideoDir { get; set; } = "main-videos";
}

public sealed class AudioConfig
{
    public string PreferredInput { get; set; } = "LINE (Yamaha AG03MK2)";
    public int SampleRate { get; set; } = 11025;
    public int DetectionWindowSeconds { get; set; } = 8;
}

public sealed class DetectionConfig
{
    public int IntervalSeconds { get; set; } = 2;
    public double ConfidenceThreshold { get; set; } = 0.62;
}

public sealed class StorageConfig
{
    public string DatabasePath { get; set; } = "data/autovj.db";
}

public sealed class FfmpegConfig
{
    public string Path { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
}
