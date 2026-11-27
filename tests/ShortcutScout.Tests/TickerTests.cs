using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary><see cref="OverlayTicker.Decide"/>: per-tick opacity, rotation and slide decisions.</summary>
[TestClass]
public class TickerTests
{
    [TestMethod]
    public void Tick_ShowsOnTheSameTickThatAReplacementPeekStarts()
    {
        // The flash: peek_seconds and re_peek_seconds both expire on this tick, so the trigger
        // handling renews the peek. Deciding opacity from the *stale* expiry hid the panel
        // for one tick even though a peek had just started.
        var settings = new OverlaySettings { Mode = "peek" };

        var shown = OverlayTicker.Decide(settings, hidden: false, idleSeconds: 0,
            peekRemainingSeconds: 6, currentOpacity: 0.9, rotationSlot: 3, peekCountedRotation: false);

        // A running peek is solid (idle_opacity), not the ambient active_opacity.
        Assert.AreEqual(settings.IdleOpacity, shown.Target, 1e-9,
            "a peek that just started must be visible on its own tick");

        var stale = OverlayTicker.Decide(settings, hidden: false, idleSeconds: 0,
            peekRemainingSeconds: 0, currentOpacity: 0.9, rotationSlot: 3, peekCountedRotation: false);

        Assert.AreEqual(0, stale.Target, 1e-9,
            "with no peek left the panel hides — this is the state that produced the flash");
    }

    [TestMethod]
    public void Tick_DoesNotCountAnAppearanceTwice_WhenAPeekAlreadyRotated()
    {
        var settings = new OverlaySettings { Mode = "peek" };

        var tick = OverlayTicker.Decide(settings, hidden: false, idleSeconds: 0,
            peekRemainingSeconds: 6, currentOpacity: 0, rotationSlot: 4, peekCountedRotation: true);

        Assert.AreEqual(4, tick.RotationSlot, "StartPeek already advanced the slot for this appearance");
        Assert.IsFalse(tick.PeekCountedRotation, "the flag is consumed by the appearance");
    }

    [TestMethod]
    public void Tick_RotatesAnIdleReveal_ThatNoPeekAccountedFor()
    {
        var settings = new OverlaySettings { Mode = "peek", IdleThresholdSeconds = 30 };

        var tick = OverlayTicker.Decide(settings, hidden: false, idleSeconds: 31,
            peekRemainingSeconds: 0, currentOpacity: 0, rotationSlot: 4, peekCountedRotation: false);

        Assert.IsTrue(tick.Target > 0, "idle should reveal the panel");
        Assert.AreEqual(5, tick.RotationSlot, "an appearance no peek counted still advances the slice");
    }

    [TestMethod]
    public void Tick_ClampsWhileObstructed_AndSkipsRotationWhileHidden()
    {
        var settings = new OverlaySettings { Mode = "peek" };

        var obstructed = OverlayTicker.Decide(settings, hidden: false, idleSeconds: 0,
            peekRemainingSeconds: 6, currentOpacity: 0.9, rotationSlot: 2,
            peekCountedRotation: false, obstructed: true);
        Assert.AreEqual(OverlayTicker.ObstructedOpacity, obstructed.Target, 1e-9);

        var hidden = OverlayTicker.Decide(settings, hidden: true, idleSeconds: 0,
            peekRemainingSeconds: 6, currentOpacity: 0.9, rotationSlot: 2, peekCountedRotation: true);
        Assert.AreEqual(0, hidden.Target, 1e-9, "manual hide wins over everything");
        Assert.IsFalse(hidden.Slide, "a hidden panel must not consume slices");
        Assert.AreEqual(2, hidden.RotationSlot);
        Assert.IsFalse(hidden.PeekCountedRotation, "a hidden tick clears the flag");
    }

    [TestMethod]
    public void Tick_NeverSlidesOnTheOpeningTick_EvenWhenTheCallersClockIsStale()
    {
        // A reviewer's worry, and a fair one: the window could hand over a stale "time since
        // slide" on the first tick of an appearance. `Decide` owns the fresh-appearance rule
        // so the rows cannot change the instant the panel arrives, whatever the caller passes.
        var settings = new OverlaySettings { RotateSeconds = 4 };

        var tick = OverlayTicker.Decide(settings, hidden: false, idleSeconds: 0,
            peekRemainingSeconds: 6, currentOpacity: 0, rotationSlot: 3,
            peekCountedRotation: false, obstructed: false, secondsSinceSlide: 999);

        Assert.IsTrue(tick.FreshAppearance);
        Assert.IsFalse(tick.Slide, "the opening tick must not change the rows");
        Assert.AreEqual(4, tick.RotationSlot, "the appearance advances the slice exactly once");
    }

    [TestMethod]
    public void Tick_SlidesAfterTheInterval_WhileStayingVisible()
    {
        var settings = new OverlaySettings { RotateSeconds = 4 };

        var tick = OverlayTicker.Decide(settings, hidden: false, idleSeconds: 0,
            peekRemainingSeconds: 2, currentOpacity: 0.9, rotationSlot: 7,
            peekCountedRotation: false, obstructed: false, secondsSinceSlide: 4);

        Assert.IsFalse(tick.FreshAppearance);
        Assert.IsTrue(tick.Slide);
        Assert.AreEqual(8, tick.RotationSlot);
    }
}
