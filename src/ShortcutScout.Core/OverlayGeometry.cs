namespace ShortcutScout.Core;

public readonly record struct Rect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;

    public bool Overlaps(double cx, double cy, double margin) =>
        cx >= X - margin && cx <= Right + margin &&
        cy >= Y - margin && cy <= Bottom + margin;

    /// <summary>Distance from a point to the nearest edge of this rectangle (0 if inside).</summary>
    public double Clearance(double cx, double cy)
    {
        var dx = Math.Max(0, Math.Max(X - cx, cx - Right));
        var dy = Math.Max(0, Math.Max(Y - cy, cy - Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

public readonly record struct Placement(double X, double Y, bool Escaped)
{
    public static Placement Stay(Rect r) => new(r.X, r.Y, true);
}

public static class OverlayGeometry
{
    /// <summary>Breathing room kept between the cursor and the overlay.</summary>
    public const double DefaultMargin = 72;

    /// <summary>How far the panel sits from the screen edge when docked.</summary>
    public const double DefaultInset = 20;

    /// <summary>
    /// How far the cursor must come before the panel relocates. Without this the
    /// panel flickers between positions when the cursor sits on the boundary.
    /// </summary>
    public const double DefaultHysteresis = 60;

    /// <summary>
    /// Choose a docked position for the overlay.
    ///
    /// The panel jumps to the slot that is FURTHEST from the cursor, not the nearest
    /// free one. Choosing the nearest escape anchors it to whichever corner it started
    /// in — it hops along one edge forever instead of using the screen — and leaves it
    /// permanently in the way of whatever is being read on that side.
    /// </summary>
    public static Placement Place(
        Rect overlay, double cx, double cy, Rect work,
        double margin = DefaultMargin, double inset = DefaultInset,
        double hysteresis = DefaultHysteresis)
    {
        var w = Math.Min(overlay.W, Math.Max(0, work.W - inset * 2));
        var h = Math.Min(overlay.H, Math.Max(0, work.H - inset * 2));

        var loX = work.X + inset;
        var hiX = Math.Max(loX, work.Right - w - inset);
        var loY = work.Y + inset;
        var hiY = Math.Max(loY, work.Bottom - h - inset);

        // Already comfortably out of the cursor's way: leave it exactly where it is.
        if (overlay.Clearance(cx, cy) >= hysteresis) return Placement.Stay(overlay);

        var xs = new[] { loX, (loX + hiX) / 2, hiX };
        var ys = new[] { loY, (loY + hiY) / 2, hiY };

        (double X, double Y)? best = null;
        var bestClearance = double.MinValue;
        var bestDistance = double.MinValue;

        foreach (var x in xs)
        {
            foreach (var y in ys)
            {
                var candidate = new Rect(x, y, w, h);
                if (candidate.Overlaps(cx, cy, margin)) continue;

                var clearance = candidate.Clearance(cx, cy);
                var distance = Distance(x + w / 2, y + h / 2, cx, cy);

                // Furthest wins; distance from the cursor centre breaks ties.
                if (clearance > bestClearance + 0.001 ||
                    (Math.Abs(clearance - bestClearance) <= 0.001 && distance > bestDistance))
                {
                    bestClearance = clearance;
                    bestDistance = distance;
                    best = (x, y);
                }
            }
        }

        if (best is { } chosen) return new Placement(chosen.X, chosen.Y, true);

        // Defensive: a very large panel on a small work area may find no docked
        // position that clears the cursor. Fall back to a finer search before
        // admitting failure so the caller can fade out instead of covering the mouse.
        if (TryFinerSearch(overlay, cx, cy, work, w, h, margin, inset) is { } fallback)
            return fallback;

        return new Placement(overlay.X, overlay.Y, false);
    }

    private static Placement? TryFinerSearch(
        Rect overlay, double cx, double cy, Rect work, double w, double h,
        double margin, double inset)
    {
        var loX = work.X + inset;
        var hiX = Math.Max(loX, work.Right - w - inset);
        var loY = work.Y + inset;
        var hiY = Math.Max(loY, work.Bottom - h - inset);

        (double X, double Y)? best = null;
        var bestClearance = double.MinValue;

        for (var i = 0; i <= 12; i++)
        {
            for (var j = 0; j <= 12; j++)
            {
                var x = loX + (hiX - loX) * i / 12.0;
                var y = loY + (hiY - loY) * j / 12.0;
                var candidate = new Rect(x, y, w, h);
                if (candidate.Overlaps(cx, cy, margin)) continue;

                var clearance = candidate.Clearance(cx, cy);
                if (clearance > bestClearance + 0.001)
                {
                    bestClearance = clearance;
                    best = (x, y);
                }
            }
        }

        return best is { } b ? new Placement(b.X, b.Y, true) : null;
    }

    /// <summary>Convenience wrapper returning just the coordinates.</summary>
    public static (double X, double Y) RunAway(
        Rect overlay, double cx, double cy, Rect work,
        double margin = DefaultMargin, double inset = DefaultInset,
        double hysteresis = DefaultHysteresis)
    {
        var p = Place(overlay, cx, cy, work, margin, inset, hysteresis);
        return (p.X, p.Y);
    }

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
