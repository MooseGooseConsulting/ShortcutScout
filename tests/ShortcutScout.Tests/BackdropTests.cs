using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary>Automatic backdrop contrast: WCAG maths and the halo theme.</summary>
[TestClass]
public class BackdropTests
{
    [TestMethod]
    public void Contrast_MatchesWcagReferencePoints()
    {
        var white = Contrast.RelativeLuminance(255, 255, 255);
        var black = Contrast.RelativeLuminance(0, 0, 0);

        Assert.AreEqual(21.0, Contrast.Ratio(white, black), 0.01);
        Assert.AreEqual(1.0, Contrast.Ratio(white, white), 0.01);
    }

    [TestMethod]
    public void NeonGreenText_ClearsWcagAaa_AgainstItsHalo()
    {
        // The halo, not a card, is what the glyphs sit on now.
        // Both palettes must carry their own weight: neon on dark, deep green on light.
        foreach (var stats in new[] { new BackdropStats(0, 0, 0, 5), new BackdropStats(255, 255, 255, 5) })
        {
            var theme = BackdropAdaptation.ThemeFor(stats);
            Assert.IsTrue(theme.KeyContrastOnHalo >= 7.0,
                $"keys only {theme.KeyContrastOnHalo:0.0}:1 on a luma-{stats.Luma255:0} backdrop");
            Assert.IsTrue(theme.ActionContrastOnHalo >= 7.0,
                $"actions only {theme.ActionContrastOnHalo:0.0}:1 on a luma-{stats.Luma255:0} backdrop");
        }
    }

    [TestMethod]
    public void BusyBackdrop_GetsAHavierHaloThanAFlatOne()
    {
        var flat = BackdropAdaptation.ThemeFor(new BackdropStats(200, 200, 200, Spread: 5));
        var busy = BackdropAdaptation.ThemeFor(new BackdropStats(200, 200, 200, Spread: 70));

        Assert.IsTrue(busy.HaloBlur > flat.HaloBlur,
            $"busy content needs a thicker halo (flat {flat.HaloBlur}, busy {busy.HaloBlur})");
    }

    [TestMethod]
    public void Halo_IsAlwaysStrongEnoughToSeparateTheGlyphs()
    {
        // Sweep realistic backdrops: pure black through pure white, flat and busy.
        foreach (var luma in new double[] { 0, 16, 32, 64, 96, 128, 160, 192, 224, 255 })
        {
            foreach (var spread in new double[] { 2, 25, 60 })
            {
                var theme = BackdropAdaptation.ThemeFor(new BackdropStats(luma, luma, luma, spread));

                Assert.IsTrue(theme.HaloOpacity >= 0.85,
                    $"halo too faint at luma={luma} spread={spread}: {theme.HaloOpacity}");
                Assert.IsTrue(theme.HaloBlur >= 2.0 && theme.HaloBlur <= 8.0,
                    $"halo blur out of range at luma={luma} spread={spread}: {theme.HaloBlur}");
                Assert.IsTrue(theme.KeyContrastOnHalo >= 7.0,
                    $"keys would vanish at luma={luma} spread={spread}: {theme.KeyContrastOnHalo:0.0}:1");
            }
        }
    }
}
