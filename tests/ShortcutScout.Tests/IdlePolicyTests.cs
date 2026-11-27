using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary><see cref="IdlePolicy"/> rules: opacity per mode, idle, re-peek, fades, slide timing, keys column.</summary>
[TestClass]
public class IdlePolicyTests
{
    [TestMethod]
    public void Idle_ThresholdIsExclusiveAt30Seconds()
    {
        var s = new OverlaySettings();
        Assert.AreEqual(0.0, IdlePolicy.OpacityFor(s, hidden: false, idleSeconds: 29, peekRemainingSeconds: 0));
        Assert.AreEqual(1.0, IdlePolicy.OpacityFor(s, hidden: false, idleSeconds: 30, peekRemainingSeconds: 0));
        Assert.AreEqual(1.0, IdlePolicy.OpacityFor(s, hidden: false, idleSeconds: 600, peekRemainingSeconds: 0));
        Assert.IsTrue(IdlePolicy.IsIdle(30, 30));
        Assert.IsFalse(IdlePolicy.IsIdle(29, 30));
    }

    [TestMethod]
    public void PeekMode_IsHiddenWhileWorking_AndShowsOnAPeek()
    {
        var s = new OverlaySettings { Mode = "peek" };

        // Working, no peek: out of the way entirely — this is what stops the panel
        // sitting in the field of view being both unreadable and distracting.
        Assert.AreEqual(0.0, IdlePolicy.OpacityFor(s, false, 0, 0));

        // App switch: fully legible for the peek window.
        Assert.AreEqual(1.0, IdlePolicy.OpacityFor(s, false, 0, peekRemainingSeconds: 3));
    }

    [TestMethod]
    public void AmbientMode_IsAlwaysReadable_NeverInTheDeadZone()
    {
        var s = new OverlaySettings { Mode = "ambient" };
        var opacity = IdlePolicy.OpacityFor(s, false, 0, 0);

        Assert.IsTrue(opacity >= IdlePolicy.ReadableFloor,
            $"ambient resting opacity {opacity} is below the readable floor");
    }

    [TestMethod]
    public void NoMode_ProducesAnUnreadableMidOpacity()
    {
        // The original complaint: a panel that is too faint to read yet still in the way.
        foreach (var mode in new[] { "peek", "ambient", "nonsense" })
        {
            var s = new OverlaySettings { Mode = mode };
            foreach (var idle in new[] { 0, 29, 30, 600 })
            {
                foreach (var peek in new[] { 0.0, 2.0 })
                {
                    var o = IdlePolicy.OpacityFor(s, false, idle, peek);
                    Assert.IsTrue(o == 0 || o >= IdlePolicy.ReadableFloor,
                        $"mode={mode} idle={idle} peek={peek} gave unreadable opacity {o}");
                }
            }
        }
    }

    [TestMethod]
    public void Toggle_HidesCompletely_ThenRestores()
    {
        var s = new OverlaySettings { Mode = "ambient" };
        var t = new ToggleState();
        Assert.IsFalse(t.Hidden);

        var visible = IdlePolicy.OpacityFor(s, t.Hidden, 0, 0);
        Assert.IsTrue(visible >= IdlePolicy.ReadableFloor, "visible state must be readable");

        t.Toggle();
        Assert.IsTrue(t.Hidden);
        Assert.AreEqual(0.0, IdlePolicy.OpacityFor(s, t.Hidden, 999, 0));
        Assert.AreEqual(0.0, IdlePolicy.OpacityFor(s, t.Hidden, 0, 5));

        t.Toggle();
        Assert.IsFalse(t.Hidden);
        Assert.AreEqual(1.0, IdlePolicy.OpacityFor(s, t.Hidden, 999, 0));
    }

    [TestMethod]
    public void FadeOutIsFasterThanFadeIn()
    {
        var s = new OverlaySettings { FadeInMs = 200, FadeOutMs = 180 };
        Assert.AreEqual(200, IdlePolicy.FadeDurationMs(s, 0.0, 1.0));
        Assert.AreEqual(180, IdlePolicy.FadeDurationMs(s, 1.0, 0.0));
    }

    [TestMethod]
    public void RePeek_FiresOnlyAfterTheInterval_AndNeverWhileHidden()
    {
        var settings = new OverlaySettings { RePeekSeconds = 90 };

        Assert.IsFalse(IdlePolicy.RePeekDue(settings, hidden: false, secondsSinceReveal: 89));
        Assert.IsTrue(IdlePolicy.RePeekDue(settings, hidden: false, secondsSinceReveal: 90));
        Assert.IsFalse(IdlePolicy.RePeekDue(settings, hidden: true, secondsSinceReveal: 600));
    }

    [TestMethod]
    public void Idle_FallsBackToPointerStillness_WhenOsIdleNeverGrows()
    {
        // Measured on this machine: GetLastInputInfo sat at 0-2s for 40s on an untouched
        // desktop, so the 30s reveal could never fire. Pointer stillness has to rescue it.
        Assert.AreEqual(31, IdlePolicy.CombineIdle(osIdleSeconds: 0, pointerStillSeconds: 31.4));
        Assert.AreEqual(40, IdlePolicy.CombineIdle(osIdleSeconds: 40, pointerStillSeconds: 3));
        Assert.AreEqual(2, IdlePolicy.CombineIdle(osIdleSeconds: 2, pointerStillSeconds: 2));
        Assert.AreEqual(0, IdlePolicy.CombineIdle(0, double.NaN));
        Assert.AreEqual(0, IdlePolicy.CombineIdle(0, -5));
    }

    [TestMethod]
    public void RePeek_ZeroDisablesPeriodicReveals()
    {
        var settings = new OverlaySettings { RePeekSeconds = 0 };
        Assert.IsFalse(IdlePolicy.RePeekDue(settings, hidden: false, secondsSinceReveal: 10_000));
    }

    [TestMethod]
    public void KeysColumn_ScalesWithFontUnlessExplicitlySet()
    {
        var auto = IdlePolicy.KeysColumnFor(new OverlaySettings { FontSize = 13 });
        var bigger = IdlePolicy.KeysColumnFor(new OverlaySettings { FontSize = 20 });
        var explicitWidth = IdlePolicy.KeysColumnFor(new OverlaySettings { FontSize = 20, KeysColumnWidth = 300 });

        Assert.IsTrue(bigger > auto, $"column should grow with the font ({auto} -> {bigger})");
        Assert.AreEqual(300, explicitWidth);
    }

    [TestMethod]
    public void Slide_FiresOnlyWhileVisible_AndOnlyAfterTheFullInterval()
    {
        var settings = new OverlaySettings { RotateSeconds = 8 };

        Assert.IsFalse(IdlePolicy.SlideDue(settings, visible: true, secondsSinceSlide: 7.9));
        Assert.IsTrue(IdlePolicy.SlideDue(settings, visible: true, secondsSinceSlide: 8));
        Assert.IsFalse(IdlePolicy.SlideDue(settings, visible: false, secondsSinceSlide: 60),
            "a hidden panel must not consume slices");
    }

    [TestMethod]
    public void Slide_ZeroHoldsTheListStill()
    {
        var settings = new OverlaySettings { RotateSeconds = 0 };
        Assert.IsFalse(IdlePolicy.SlideDue(settings, visible: true, secondsSinceSlide: 600));
    }

    [TestMethod]
    public void Slide_DefaultStepsWhileThePanelIsUp()
    {
        // The panel should keep teaching during a long peek, not freeze on the first slice.
        var settings = new OverlaySettings();
        Assert.IsTrue(settings.RotateSeconds > 0);
        Assert.IsTrue(IdlePolicy.SlideDue(settings, visible: true, secondsSinceSlide: settings.RotateSeconds));
    }
}
