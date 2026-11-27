using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary>Edge cases in what counts as idle and what counts as a window switch.</summary>
[TestClass]
public class RevealRulesTests
{
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-5)]
    public void AnIdleThresholdOfZero_DisablesTheIdleReveal_InsteadOfPinningThePanelOn(int threshold)
    {
        var settings = new OverlaySettings { Mode = "peek", IdleThresholdSeconds = threshold };

        Assert.IsFalse(IdlePolicy.IsIdle(0, threshold));
        Assert.IsFalse(IdlePolicy.IsIdle(int.MaxValue, threshold));
        Assert.AreEqual(IdlePolicy.HiddenOpacity,
            IdlePolicy.OpacityFor(settings, hidden: false, idleSeconds: 3600, peekRemainingSeconds: 0),
            "peek mode with no peek running stays out of the way");
    }

    [TestMethod]
    public void APositiveIdleThreshold_StillReveals()
    {
        var settings = new OverlaySettings { Mode = "peek", IdleThresholdSeconds = 30 };

        Assert.IsFalse(IdlePolicy.IsIdle(29, 30));
        Assert.IsTrue(IdlePolicy.IsIdle(30, 30));
        Assert.AreEqual(settings.IdleOpacity,
            IdlePolicy.OpacityFor(settings, hidden: false, idleSeconds: 30, peekRemainingSeconds: 0));
    }

    [TestMethod]
    public void NoForegroundWindow_IsNotASwitch_AndDoesNotForgetTheRealOne()
    {
        const long editor = 0x1234;

        // Mid alt-tab Windows briefly reports no foreground window at all.
        var transit = RevealPolicy.ObserveWindow(editor, RevealPolicy.NoWindow, hidden: false);
        Assert.IsFalse(transit.Changed);
        Assert.AreEqual(editor, transit.WindowKey, "the last real window is still the reference");

        // Landing back on the same window is therefore not a change either...
        Assert.IsFalse(RevealPolicy.ObserveWindow(transit.WindowKey, editor, hidden: false).Changed);

        // ...while arriving somewhere new still is.
        Assert.IsTrue(RevealPolicy.ObserveWindow(transit.WindowKey, 0x9999, hidden: false).Changed);
    }
}
