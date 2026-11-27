using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary><see cref="OverlayGeometry"/>: dodging the cursor and staying on screen.</summary>
[TestClass]
public class GeometryTests
{
    [TestMethod]
    public void RunAway_MovesClearOfCursor_AndStaysOnScreen()
    {
        var work = new Rect(0, 0, 1920, 1040);
        var (x, y) = OverlayGeometry.RunAway(new Rect(100, 100, 300, 200), 150, 150, work);

        Assert.IsFalse(new Rect(x, y, 300, 200).Overlaps(150, 150, 10), "still overlapping the cursor");
        Assert.IsTrue(x >= 0 && y >= 0);
        Assert.IsTrue(x + 300 <= 1920 + 0.001, $"x overflow: {x}");
        Assert.IsTrue(y + 200 <= 1040 + 0.001, $"y overflow: {y}");
    }

    [TestMethod]
    public void Place_EscapesFromTheCaseThatFailedOnScreen()
    {
        // Real failing capture: overlay at 40,40 sized 380x299 with the cursor parked
        // dead centre. The old radial push clamped to 0,0 and left the cursor inside.
        var work = new Rect(0, 0, 1920, 1040);
        var overlay = new Rect(40, 40, 380, 299);
        var cursorX = 40 + 380 / 2.0;   // 230
        var cursorY = 40 + 299 / 2.0;   // 189.5

        var p = OverlayGeometry.Place(overlay, cursorX, cursorY, work);

        Assert.IsTrue(p.Escaped, "must report an escape");
        Assert.IsFalse(new Rect(p.X, p.Y, 380, 299).Overlaps(cursorX, cursorY, 10),
            $"cursor still inside overlay at ({p.X},{p.Y})");
    }

    [TestMethod]
    public void Place_SweepsTheWholeWorkAreaWithoutEverCoveringTheCursor()
    {
        var work = new Rect(0, 0, 1920, 1040);
        var overlay = new Rect(40, 40, 380, 299);

        // Walk the cursor across the screen; the overlay must never end up under it.
        for (var cx = 0; cx <= 1920; cx += 40)
        {
            for (var cy = 0; cy <= 1040; cy += 40)
            {
                var p = OverlayGeometry.Place(overlay, cx, cy, work);
                if (!p.Escaped) continue; // allowed only if genuinely impossible
                Assert.IsFalse(new Rect(p.X, p.Y, 380, 299).Overlaps(cx, cy, 10),
                    $"covered the cursor at ({cx},{cy}) by moving to ({p.X},{p.Y})");
                Assert.IsTrue(p.X >= 0 && p.Y >= 0);
                Assert.IsTrue(p.X + 380 <= 1920 + 0.5, $"off-screen right at ({p.X},{p.Y})");
                Assert.IsTrue(p.Y + 299 <= 1040 + 0.5, $"off-screen bottom at ({p.X},{p.Y})");
            }
        }
    }

    [TestMethod]
    public void Place_SnapsToAScreenEdgeOrCorner()
    {
        var work = new Rect(0, 0, 1920, 1040);
        var overlay = new Rect(40, 40, 380, 299);
        const double inset = OverlayGeometry.DefaultInset;

        foreach (var (cx, cy) in new[] { (40.0, 40.0), (200.0, 200.0), (960.0, 520.0), (1500.0, 300.0) })
        {
            var p = OverlayGeometry.Place(overlay, cx, cy, work);

            // Staying put is legitimate; if it does move, it must land on a docked slot.
            if (Math.Abs(p.X - overlay.X) > 0.5 || Math.Abs(p.Y - overlay.Y) > 0.5)
            {
                var dockedX = Math.Abs(p.X - inset) < 0.5 ||
                              Math.Abs(p.X + 380 - (1920 - inset)) < 0.5 ||
                              Math.Abs(p.X - (1920 - 380) / 2.0) < 1.0;
                var dockedY = Math.Abs(p.Y - inset) < 0.5 ||
                              Math.Abs(p.Y + 299 - (1040 - inset)) < 0.5 ||
                              Math.Abs(p.Y - (1040 - 299) / 2.0) < 1.0;

                Assert.IsTrue(dockedX && dockedY,
                    $"moved to a non-docked spot ({p.X},{p.Y}) at cursor ({cx},{cy})");
            }
            Assert.IsFalse(new Rect(p.X, p.Y, 380, 299).Overlaps(cx, cy, 10),
                $"covered the cursor at ({cx},{cy})");
        }
    }

    [TestMethod]
    public void Place_ReportsFailureWhenItCannotFitAnywhere()
    {
        // Overlay larger than the work area: no position can clear the cursor.
        var work = new Rect(0, 0, 200, 200);
        var overlay = new Rect(0, 0, 400, 400);
        var p = OverlayGeometry.Place(overlay, 100, 100, work);
        Assert.IsFalse(p.Escaped, "should admit it cannot escape so the caller can fade out");
    }

    [TestMethod]
    public void DoesNotMove_WhenCursorIsFarAndAlreadyDocked()
    {
        // Already flush in the top-left corner: no reason to twitch.
        var inset = OverlayGeometry.DefaultInset;
        var (x, y) = OverlayGeometry.RunAway(
            new Rect(inset, inset, 300, 200), 1500, 900, new Rect(0, 0, 1920, 1040));

        Assert.AreEqual(inset, x);
        Assert.AreEqual(inset, y);
    }

    [TestMethod]
    public void StaysPut_WhenNothingIsInTheWay()
    {
        // Relocating a widget nobody is covering is motion for its own sake.
        var (x, y) = OverlayGeometry.RunAway(
            new Rect(100, 100, 300, 200), 1500, 900, new Rect(0, 0, 1920, 1040));

        Assert.AreEqual(100, x);
        Assert.AreEqual(100, y);
    }

    [TestMethod]
    public void EscapesAcrossTheScreen_InsteadOfCrawlingAlongOneEdge()
    {
        // The regression that mattered on screen: the panel sat on the left and only
        // ever shuffled up and down that edge, so it was permanently in the way.
        var work = new Rect(0, 0, 1920, 1040);
        var overlay = new Rect(20, 20, 380, 299);
        var p = OverlayGeometry.Place(overlay, 200, 160, work);

        Assert.IsTrue(p.Escaped);
        Assert.IsTrue(p.X > work.W / 2,
            $"cursor on the left should throw the panel to the far side, not to ({p.X},{p.Y})");
    }

    [TestMethod]
    public void RepeatsWithoutOscillating_WhenTheCursorSitsStill()
    {
        // A wobbling hysteresis boundary would make the panel twitch every tick.
        var work = new Rect(0, 0, 1920, 1040);
        var overlay = new Rect(20, 20, 380, 299);
        var first = OverlayGeometry.Place(overlay, 200, 160, work);

        var settled = new Rect(first.X, first.Y, 380, 299);
        var again = OverlayGeometry.Place(settled, 200, 160, work);

        Assert.AreEqual(first.X, again.X);
        Assert.AreEqual(first.Y, again.Y);
    }

    [TestMethod]
    public void RunAway_EscapesWhenPinnedInACorner()
    {
        // Overlay fills the work area's top-left and cursor sits inside it near the corner.
        var work = new Rect(0, 0, 1920, 1040);
        var overlay = new Rect(0, 0, 380, 260);
        var (x, y) = OverlayGeometry.RunAway(overlay, 5, 5, work);
        Assert.IsFalse(new Rect(x, y, 380, 260).Overlaps(5, 5, 10));
        Assert.IsTrue(x >= 0 && y >= 0);
    }

    [TestMethod]
    public void Place_CyclesThroughClearPositions_InsteadOfAlwaysTakingTheFarCorner()
    {
        // Always picking the single furthest slot parked the panel in the same two corners.
        var work = new Rect(0, 0, 1920, 1040);
        var overlay = new Rect(20, 20, 460, 200);

        var seen = new HashSet<(double, double)>();
        for (var variety = 0; variety < 3; variety++)
        {
            var p = OverlayGeometry.Place(overlay, 200, 160, work, variety: variety);
            Assert.IsTrue(p.Escaped);
            Assert.IsFalse(new Rect(p.X, p.Y, 460, 200).Overlaps(200, 160, 72),
                $"variety {variety} still covers the cursor at ({p.X},{p.Y})");
            seen.Add((p.X, p.Y));
        }

        Assert.AreEqual(3, seen.Count, "the first three dodges should use three different positions");
    }

    [TestMethod]
    public void Place_RedocksAfterThePanelGrowsPastTheEdge()
    {
        // Resizing while docked bottom-right leaves Left/Top alone and pushes the text
        // off screen. "Far from the cursor" alone must not count as acceptable.
        var work = new Rect(0, 0, 1920, 1040);
        var grown = new Rect(1500, 900, 460, 300); // right = 1960, bottom = 1200: both overflow

        var p = OverlayGeometry.Place(grown, 100, 100, work);

        Assert.IsTrue(p.Escaped);
        Assert.IsTrue(p.X + 460 <= 1920 + 0.5, $"still off the right edge at {p.X}");
        Assert.IsTrue(p.Y + 300 <= 1040 + 0.5, $"still off the bottom edge at {p.Y}");
    }

    [TestMethod]
    public void Place_KeepsAVeryWidePanelOnScreen_ByShrinkingTheInset()
    {
        // A panel as wide as the work area can't honour a 20px inset on both sides; docking
        // it at the inset would report success while the right edge overflowed.
        var work = new Rect(0, 0, 800, 600);
        var wide = new Rect(0, 0, 800, 200);

        var p = OverlayGeometry.Place(wide, 400, 300, work);

        Assert.IsTrue(p.Escaped);
        Assert.IsTrue(p.X >= -0.5, $"off the left edge at {p.X}");
        Assert.IsTrue(p.X + 800 <= 800 + 0.5, $"off the right edge at {p.X}");
        Assert.IsTrue(p.Y + 200 <= 600 + 0.5, $"off the bottom edge at {p.Y}");
    }

    [TestMethod]
    public void Place_LeavesAnOnScreenPanelAlone_EvenIfItIsNotDocked()
    {
        var work = new Rect(0, 0, 1920, 1040);
        var floating = new Rect(500, 300, 460, 200);

        var p = OverlayGeometry.Place(floating, 1800, 1000, work);

        Assert.AreEqual(500, p.X);
        Assert.AreEqual(300, p.Y);
    }
}
