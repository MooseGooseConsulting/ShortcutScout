using System.Text.Json.Serialization;

namespace ShortcutScout.Core;

/// <summary>
/// How the overlay earns its screen time.
/// </summary>
public enum OverlayMode
{
    /// <summary>
    /// Hidden while you work; appears briefly when you switch apps, and stays
    /// up while you are away. Nothing lingers in your field of view.
    /// </summary>
    Peek,

    /// <summary>
    /// Always present, but solid enough to actually read (the PowerToys model,
    /// default 90%). Never parked at a low opacity that is both unreadable and
    /// distracting.
    /// </summary>
    Ambient,
}

public sealed class OverlaySettings
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "peek";

    [JsonPropertyName("idle_threshold_seconds")] public int IdleThresholdSeconds { get; set; } = 30;

    [JsonPropertyName("peek_seconds")] public double PeekSeconds { get; set; } = 6;

    /// <summary>
    /// Resting opacity in ambient mode. The research floor for readable body text
    /// over uncontrolled content is ~0.54; 0.90 is what PowerToys ships.
    /// </summary>
    [JsonPropertyName("active_opacity")] public double ActiveOpacity { get; set; } = 0.90;

    [JsonPropertyName("idle_opacity")] public double IdleOpacity { get; set; } = 1.0;

    [JsonPropertyName("fade_in_ms")] public int FadeInMs { get; set; } = 200;

    [JsonPropertyName("fade_out_ms")] public int FadeOutMs { get; set; } = 180;
}

public static class IdlePolicy
{
    public const double HiddenOpacity = 0.0;

    /// <summary>
    /// The opacity a translucent panel must reach before body text is legible over a
    /// worst-case (pure white) backdrop: 0.54 black scrim yields ~4.6:1 against white.
    /// Anything below this is the "dead zone" — unreadable but still in the way.
    /// </summary>
    public const double ReadableFloor = 0.54;

    public static OverlayMode ParseMode(string? mode) =>
        string.Equals(mode, "ambient", StringComparison.OrdinalIgnoreCase)
            ? OverlayMode.Ambient
            : OverlayMode.Peek;

    public static bool IsIdle(int idleSeconds, int threshold) => idleSeconds >= threshold;

    /// <summary>
    /// Target opacity for the current moment. Deliberately never returns a value
    /// between 0 and <see cref="ReadableFloor"/>: the panel is either out of the way
    /// or legible, never a half-visible smudge.
    /// </summary>
    public static double OpacityFor(
        OverlaySettings settings, bool hidden, int idleSeconds, double peekRemainingSeconds)
    {
        if (hidden) return HiddenOpacity;

        if (IsIdle(idleSeconds, settings.IdleThresholdSeconds)) return settings.IdleOpacity;

        if (peekRemainingSeconds > 0) return settings.IdleOpacity;

        return ParseMode(settings.Mode) switch
        {
            OverlayMode.Ambient => Math.Max(settings.ActiveOpacity, ReadableFloor),
            _ => HiddenOpacity,
        };
    }

    /// <summary>Fade out is quicker than fade in: leaving needs less attention than arriving.</summary>
    public static int FadeDurationMs(OverlaySettings settings, double from, double to) =>
        to > from ? settings.FadeInMs : settings.FadeOutMs;
}

public sealed class ToggleState
{
    public bool Hidden { get; private set; }
    public void Toggle() => Hidden = !Hidden;
    public void Show() => Hidden = false;
    public void Hide() => Hidden = true;
}
