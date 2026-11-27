namespace ShortcutScout.Core;

/// <summary>What one timer tick decided for the overlay.</summary>
/// <param name="FreshAppearance">The panel is becoming visible this tick; the slide clock restarts.</param>
/// <param name="Slide">Step to the next slice while staying visible.</param>
public readonly record struct OverlayTick(
    double Target, long RotationSlot, bool PeekCountedRotation, bool FreshAppearance, bool Slide);

/// <summary>
/// The decisions a single 500ms tick makes: how visible the panel should be, and which
/// rotation slice the next appearance uses.
///
/// This lived inline in the window, where ordering bugs were invisible to the test suite.
/// The order is the point: opacity must be decided from the peek state *after* the tick's
/// reveal triggers have run, and rotation must advance *before* the rows are chosen. A peek
/// that starts on the same tick the previous one expires has to be visible immediately —
/// deciding opacity first pinned the old expiry and hid the panel for a tick, which reads as
/// a flash.
/// </summary>
public static class OverlayTicker
{
    /// <summary>Below this the panel counts as invisible for rotation bookkeeping.</summary>
    public const double InvisibleThreshold = 0.001;

    /// <summary>Ceiling while the cursor is on top of the panel and hiding it would confuse.</summary>
    public const double ObstructedOpacity = 0.14;

    /// <param name="peekRemainingSeconds">
    /// Time left in the current peek, read *after* this tick's triggers ran. Pass 0 when
    /// nothing is scheduled.
    /// </param>
    /// <param name="currentOpacity">What is on screen right now, before this tick is applied.</param>
    /// <param name="peekCountedRotation">
    /// True when a peek already advanced <paramref name="rotationSlot"/> for this appearance.
    /// </param>
    /// <param name="secondsSinceSlide">
    /// Time since the last slice change *or* since this appearance began, whichever is later.
    /// The caller resets that clock whenever <see cref="OverlayTick.FreshAppearance"/> or
    /// <see cref="OverlayTick.Slide"/> comes back true.
    /// </param>
    public static OverlayTick Decide(
        OverlaySettings settings,
        bool hidden,
        int idleSeconds,
        double peekRemainingSeconds,
        double currentOpacity,
        long rotationSlot,
        bool peekCountedRotation,
        bool obstructed = false,
        double secondsSinceSlide = 0)
    {
        var target = IdlePolicy.OpacityFor(settings, hidden, idleSeconds, peekRemainingSeconds);
        if (obstructed && !hidden) target = Math.Min(target, ObstructedOpacity);

        var visible = target > InvisibleThreshold;
        var freshAppearance = visible && currentOpacity <= InvisibleThreshold;

        // Rotation counts appearances. A peek advances the slot when it starts; an appearance
        // no peek accounted for — an idle reveal, or an un-hide whose peek the anti-strobe
        // floor refused — advances it here. The flag stops one appearance being counted twice.
        if (freshAppearance)
        {
            if (!peekCountedRotation) rotationSlot++;
            peekCountedRotation = false;
        }
        else if (!visible)
        {
            peekCountedRotation = false;
        }

        // Gentle rotation: step through slices while the panel stays up. Never on the opening
        // tick of an appearance, however stale the caller's clock looks — the rows must not
        // change the instant the panel arrives.
        var slide = !freshAppearance && IdlePolicy.SlideDue(settings, visible, secondsSinceSlide);
        if (slide) rotationSlot++;

        return new OverlayTick(target, rotationSlot, peekCountedRotation, freshAppearance, slide);
    }
}
