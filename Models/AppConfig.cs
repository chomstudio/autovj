namespace AutoVJ.Models;

public sealed class AppConfig
{
    public ServerConfig Server { get; } = new();
    public MediaConfig Media { get; } = new();
    public AudioConfig Audio { get; } = new();
    public DetectionConfig Detection { get; } = new();
    public PlaybackConfig Playback { get; } = new();
    public TransitionConfig Transition { get; } = new();
    public StorageConfig Storage { get; } = new();
    public FfmpegConfig Ffmpeg { get; } = new();
}

public sealed class ServerConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5180;
    public bool AutoOpenBrowser { get; set; } = true;
}

public sealed class MediaConfig
{
    public string InputAudioDir { get; set; } = "input-audio";
    public string MainVideoDir { get; set; } = "main-videos";
    public string MaterialVideoDir { get; set; } = "material-videos";
    public string CommonVideoFile { get; set; } = "common_movie.mp4";
}

public sealed class AudioConfig
{
    public string PreferredInput { get; set; } = "LINE (Yamaha AG03MK2)";
    public int SampleRate { get; set; } = 11025;
    public int DetectionWindowSeconds { get; set; } = 4;
}

public sealed class DetectionConfig
{
    public int IntervalSeconds { get; set; } = 1;
    public double ConfidenceThreshold { get; set; } = 0.62;
    public double TentativeConfidenceThreshold { get; set; } = 0.55;
    public int TentativeConfirmationCount { get; set; } = 2;
    public double MinimumInputDecibels { get; set; } = -48;
    public List<double> TempoRatios { get; set; } = [0.90, 0.93, 0.95, 1.00, 1.05, 1.10, 1.15, 1.17];
    public double PlaybackRateTolerance { get; set; } = 0.015;
}

public sealed class PlaybackConfig
{
    public int DetectionLostTimeoutSeconds { get; set; } = 10;
    public double ResyncToleranceSeconds { get; set; } = 2;
}

public sealed class TransitionConfig
{
    public bool Enabled { get; set; } = true;
    public int DurationMilliseconds { get; set; } = 1200;
    public bool BlendModesEnabled { get; set; } = true;
    public bool RandomizeBlendMode { get; set; } = true;
    public List<string> BlendModes { get; set; } = ["screen", "multiply", "overlay", "soft-light", "difference"];
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
