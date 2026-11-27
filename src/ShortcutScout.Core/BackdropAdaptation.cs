namespace ShortcutScout.Core;

/// <summary>
/// What the screen actually looks like right behind the overlay.
/// <paramref name="Spread"/> is the standard deviation of luminance: low means a flat
/// backdrop (a solid editor background), high means busy (text, code, images).
/// </summary>
public readonly record struct BackdropStats(double R, double G, double B, double Spread)
{
    /// <summary>Perceptual brightness 0-255, used for human-ish thresholds.</summary>
    public double Luma255 => 0.2126 * R + 0.7152 * G + 0.0722 * B;

    /// <summary>WCAG relative luminance 0-1, used for real contrast ratios.</summary>
    public double Luminance => Contrast.RelativeLuminance(R, G, B);

    /// <summary>
    /// Real desktops are almost always text-heavy; the threshold is deliberately low
    /// so the halo tightens up before glyph edges start getting lost in texture.
    /// </summary>
    public bool IsBusy => Spread >= 26;

    public bool IsFlat => Spread < 14;

    public static BackdropStats Empty => new(0, 0, 0, 0);
}

public static class Contrast
{
    private static double Channel(double c255)
    {
        var c = Math.Clamp(c255, 0, 255) / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    public static double RelativeLuminance(double r, double g, double b) =>
        0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);

    /// <summary>WCAG 2.x contrast ratio, 1:1 (identical) to 21:1 (black on white).</summary>
    public static double Ratio(double lumA, double lumB)
    {
        var hi = Math.Max(lumA, lumB);
        var lo = Math.Min(lumA, lumB);
        return (hi + 0.05) / (lo + 0.05);
    }
}

/// <summary>
/// Colours for the floating text, plus the halo that separates the glyphs from
/// whatever is behind them.
/// </summary>
public readonly record struct TextTheme(
    byte KeyR, byte KeyG, byte KeyB,
    byte ActionR, byte ActionG, byte ActionB,
    byte HaloR, byte HaloG, byte HaloB,
    double HaloOpacity, double HaloBlur)
{
    public double KeyContrastOnHalo => Contrast.Ratio(
        Contrast.RelativeLuminance(KeyR, KeyG, KeyB),
        Contrast.RelativeLuminance(HaloR, HaloG, HaloB));

    public double ActionContrastOnHalo => Contrast.Ratio(
        Contrast.RelativeLuminance(ActionR, ActionG, ActionB),
        Contrast.RelativeLuminance(HaloR, HaloG, HaloB));
}

/// <summary>
/// The panel is bare text, so contrast has to come from the text itself: neon green
/// glyphs on a dark halo. The halo is what the text actually sits on, so it — not the
/// desktop behind it — is what the contrast ratio is measured against.
///
/// The palette stays neon green over any backdrop (it reads well even on light pages
/// once the halo is doing its job); only the halo's weight adapts, because busy
/// content needs a thicker outline for letterform edges to survive.
/// </summary>
public static class BackdropAdaptation
{
    public static TextTheme ThemeFor(BackdropStats backdrop)
    {
        var blur = backdrop.IsBusy ? 6.5 : backdrop.IsFlat ? 4.0 : 5.0;

        return new TextTheme(
            KeyR: 0x39, KeyG: 0xFF, KeyB: 0x14,       // neon green
            ActionR: 0x8C, ActionG: 0xFF, ActionB: 0x7A,
            HaloR: 0x00, HaloG: 0x00, HaloB: 0x00,    // dark halo
            HaloOpacity: 1.0, HaloBlur: blur);
    }

    /// <summary>Palette used before any backdrop has been sampled.</summary>
    public static TextTheme Default => ThemeFor(BackdropStats.Empty);
}
