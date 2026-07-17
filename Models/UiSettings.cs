namespace AutoVJ.Models;

public sealed record UiSettingsRequest(
    string SelectedInputSourceId,
    int PositionOffsetMilliseconds,
    int DetectionLostTimeoutSeconds,
    double ResyncToleranceSeconds,
    double MinimumPlaybackRate,
    double MaximumPlaybackRate,
    double MinimumBpm,
    double MaximumBpm,
    bool TransitionEnabled,
    int TransitionDurationMilliseconds,
    bool BlendModesEnabled,
    bool RandomizeBlendMode,
    List<string> BlendModes,
    double GlitchConfidenceThreshold);
