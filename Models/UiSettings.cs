namespace AutoVJ.Models;

public sealed record UiSettingsRequest(
    int DetectionLostTimeoutSeconds,
    double ResyncToleranceSeconds,
    bool TransitionEnabled,
    int TransitionDurationMilliseconds,
    bool BlendModesEnabled,
    bool RandomizeBlendMode,
    List<string> BlendModes,
    double GlitchConfidenceThreshold);
