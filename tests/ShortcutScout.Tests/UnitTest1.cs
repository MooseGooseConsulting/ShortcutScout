using ShortcutScout.Core;

namespace ShortcutScout.Tests;

[TestClass]
public class Tests
{
    private static AppConfig App(string id, int priority, List<string> procs, List<string> titles, params Shortcut[] shortcuts) => new()
    {
        Id = id,
        Name = id,
        Priority = priority,
        ProcessNames = procs,
        TitleContains = titles,
        Shortcuts = shortcuts.ToList()
    };

    // Mirrors the shipped configs: browsers match by title only, so high-priority
    // web apps can't hijack every browser window.
    private static List<AppConfig> Sample() => new()
    {
        App("global", 0, new(), new()),
        App("chatgpt", 1000, new(), new() { "chatgpt.com", "ChatGPT" }, new Shortcut("Ctrl+Enter", "Send")),
        App("devin", 1000, new(), new() { "devin.ai", "Devin" }, new Shortcut("Ctrl+K", "Palette", Discovery: true), new Shortcut("Esc", "Stop")),
        App("chrome", 10, new() { "chrome" }, new() { "Google Chrome" }, new Shortcut("Ctrl+T", "New tab")),
        App("zellij", 800, new() { "zellij" }, new() { "zellij" }, new Shortcut("Ctrl+g", "Lock")),
    };

    [TestMethod]
    public void Match_ByProcess_Zellij() =>
        Assert.AreEqual("zellij", Matcher.Match(Sample(), new("zellij", "zellij.exe", "zellij"))?.Id);

    [TestMethod]
    public void Match_TitleBeatsProcess_DevinInsideChrome() =>
        // Chrome's own config also matches this title (" - Google Chrome"),
        // but Devin must win.
        Assert.AreEqual("devin", Matcher.Match(Sample(), new("chrome", "chrome.exe", "Devin - devin.ai - Google Chrome"))?.Id);

    [TestMethod]
    public void Match_ChatGptInsideChrome() =>
        Assert.AreEqual("chatgpt", Matcher.Match(Sample(), new("chrome", "chrome.exe", "ChatGPT - Google Chrome"))?.Id);

    [TestMethod]
    public void Match_PlainChromeIsNotHijackedByChatGpt()
    {
        // Regression: if ChatGPT listed "chrome" in process_names with priority 1000
        // it would win on every browser window.
        Assert.AreEqual("chrome", Matcher.Match(Sample(), new("chrome", "chrome.exe", "GitHub - Google Chrome"))?.Id);
    }

    [TestMethod]
    public void Match_UnknownProcess_ReturnsNull() =>
        Assert.IsNull(Matcher.Match(Sample(), new("notepad", "notepad.exe", "Untitled - Notepad")));

    [TestMethod]
    public void Match_HigherPriorityWinsBetweenTitleMatches()
    {
        var apps = new List<AppConfig>
        {
            App("low", 1, new(), new() { "shared" }),
            App("high", 50, new(), new() { "shared" }),
        };
        Assert.AreEqual("high", Matcher.Match(apps, new("x", "x.exe", "a shared title"))?.Id);
    }

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
    public void OverlaySettings_LoadFromConfigFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shortcuts.json"),
                """{"overlay":{"mode":"ambient","idle_threshold_seconds":45,"peek_seconds":4,"active_opacity":0.85},"apps":[]}""");

            var overlay = new ConfigStore(dir).Config.Overlay;

            Assert.AreEqual("ambient", overlay.Mode);
            Assert.AreEqual(45, overlay.IdleThresholdSeconds);
            Assert.AreEqual(4, overlay.PeekSeconds);
            Assert.AreEqual(0.85, overlay.ActiveOpacity);
        }
        finally { Directory.Delete(dir, true); }
    }

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
    public void Rotation_IsStableWithinASlot()
    {
        var all = Enumerable.Range(0, 12)
            .Select(i => new Shortcut($"K{i}", $"action {i}", Discovery: i % 2 == 0))
            .ToList();

        var slot = Rotation.SlotFor(TimeSpan.FromMinutes(3.5));
        var first = new Rotation().Select(all, 6, slot);
        var again = new Rotation().Select(all, 6, slot);

        CollectionAssert.AreEqual(
            first.Select(s => s.Keys).ToList(),
            again.Select(s => s.Keys).ToList(),
            "selection must not change between UI ticks inside one slot");
    }

    [TestMethod]
    public void Rotation_ChangesAcrossSlots()
    {
        var all = Enumerable.Range(0, 12)
            .Select(i => new Shortcut($"K{i}", $"action {i}", Discovery: i % 2 == 0))
            .ToList();

        var a = new Rotation().Select(all, 6, Rotation.SlotFor(TimeSpan.Zero));
        var b = new Rotation().Select(all, 6, Rotation.SlotFor(TimeSpan.FromMinutes(3)));

        Assert.AreNotEqual(
            string.Join(",", a.Select(s => s.Keys)),
            string.Join(",", b.Select(s => s.Keys)),
            "the list should gently change when the slot advances");
    }

    [TestMethod]
    public void Rotation_SlotAdvancesOnlyEveryThreeMinutes()
    {
        Assert.AreEqual(0, Rotation.SlotFor(TimeSpan.FromSeconds(0)));
        Assert.AreEqual(0, Rotation.SlotFor(TimeSpan.FromSeconds(179)));
        Assert.AreEqual(1, Rotation.SlotFor(TimeSpan.FromSeconds(180)));
        Assert.AreEqual(1, Rotation.SlotFor(TimeSpan.FromSeconds(359)));
        Assert.AreEqual(2, Rotation.SlotFor(TimeSpan.FromSeconds(360)));
    }

    [TestMethod]
    public void Rotation_SurfacesDiscoveryItems()
    {
        var all = new List<Shortcut>
        {
            new("A", "a"), new("B", "b"), new("C", "c"),
            new("D", "d", Discovery: true), new("E", "e", Discovery: true),
        };
        var picked = new Rotation().Select(all, 6, 0);

        Assert.AreEqual(5, picked.Count, "all five fit");
        Assert.IsTrue(picked.Any(s => s.Discovery), "discovery items must appear");
    }

    [TestMethod]
    public void Rotation_NeverExceedsMaxOrRepeatsAnEntry()
    {
        var all = Enumerable.Range(0, 9).Select(i => new Shortcut($"K{i}", $"a{i}", Discovery: i > 4)).ToList();
        var picked = new Rotation().Select(all, 4, 0);

        Assert.AreEqual(4, picked.Count);
        Assert.AreEqual(4, picked.Distinct().Count(), "no duplicate rows");
    }

    [TestMethod]
    public void Rotation_HandlesEmptyAndZeroMax()
    {
        Assert.AreEqual(0, new Rotation().Select(new List<Shortcut>(), 5, 0).Count);
        var one = new List<Shortcut> { new("A", "a") };
        Assert.AreEqual(0, new Rotation().Select(one, 0, 0).Count);
        Assert.AreEqual(1, new Rotation().Select(one, 5, 0).Count);
    }

    // ---------- automatic backdrop contrast ----------

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

    [TestMethod]
    public void Config_AddRemoveReload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(dir);
            Assert.IsTrue(store.AddShortcut("myapp", new Shortcut("Ctrl+X", "Do X")));
            Assert.IsFalse(store.AddShortcut("myapp", new Shortcut("Ctrl+X", "Do X")), "duplicate rejected");

            var reloaded = new ConfigStore(dir);
            Assert.AreEqual(1, reloaded.Config.Apps.First(a => a.Id == "myapp").Shortcuts.Count);

            Assert.IsTrue(reloaded.RemoveShortcut("myapp", "Ctrl+X"));
            Assert.IsFalse(reloaded.RemoveShortcut("myapp", "Ctrl+X"), "second remove is a no-op");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Config_LoadsAppFilesAndPriority()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "apps"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "apps", "x.json"),
                """{"id":"x","name":"X","priority":42,"title_contains":["x-thing"],"shortcuts":[{"keys":"Ctrl+1","action":"One","discovery":true}]}""");
            var store = new ConfigStore(dir);
            var app = store.Config.Apps.Single(a => a.Id == "x");

            Assert.AreEqual(42, app.Priority);
            Assert.AreEqual("x-thing", app.TitleContains.Single());
            Assert.IsTrue(app.Shortcuts.Single().Discovery);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Config_SkipsMalformedFilesInsteadOfCrashing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "apps"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "apps", "broken.json"), "{ not json ");
            File.WriteAllText(Path.Combine(dir, "apps", "good.json"),
                """{"id":"good","name":"Good","shortcuts":[{"keys":"Ctrl+G","action":"Go"}]}""");

            var store = new ConfigStore(dir);
            Assert.AreEqual(1, store.Config.Apps.Count);
            Assert.AreEqual("good", store.Config.Apps.Single().Id);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Mcp_InitializeAndToolsList()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var server = new McpServer(new ConfigStore(dir));

            var init = server.Handle("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
            StringAssert.Contains(init, "shortcutscout");

            var tools = server.Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            foreach (var name in new[] { "list_apps", "list_shortcuts", "add_shortcut", "remove_shortcut", "reload_config" })
                StringAssert.Contains(tools, name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Mcp_AddThenListRoundTripsThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-mcp2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var server = new McpServer(new ConfigStore(dir));
            var add = server.Handle("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"add_shortcut","arguments":{"app":"devin","keys":"Ctrl+Z","action":"Undo","discovery":true}}}""");
            StringAssert.Contains(add, "ok");

            // Fresh store: proves it was persisted, not just held in memory.
            var list = new McpServer(new ConfigStore(dir))
                .Handle("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_shortcuts","arguments":{"app":"devin"}}}""");
            StringAssert.Contains(list, "Ctrl+Z");
            StringAssert.Contains(list, "Undo");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Mcp_UnknownMethodReturnsJsonRpcError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-mcp3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var response = new McpServer(new ConfigStore(dir))
                .Handle("""{"jsonrpc":"2.0","id":9,"method":"nope/nope"}""");
            StringAssert.Contains(response, "\"error\"");
            StringAssert.Contains(response, "\"id\":9");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ShippedConfigsMatchPriorityIntent()
    {
        var root = FindRepoRoot();
        var store = new ConfigStore(Path.Combine(root, "config"));
        var apps = store.Config.Apps;

        Assert.IsTrue(apps.Count >= 6, $"expected the shipped app configs, found {apps.Count}");

        // The two priority apps must actually match their own browser titles.
        Assert.AreEqual("devin", Matcher.Match(apps, new("chrome", "chrome.exe", "Devin - Google Chrome"))?.Id);
        Assert.AreEqual("chatgpt", Matcher.Match(apps, new("chrome", "chrome.exe", "ChatGPT - Google Chrome"))?.Id);

        // ...without letting them claim a normal browser window.
        Assert.AreEqual("chrome", Matcher.Match(apps, new("chrome", "chrome.exe", "News - Google Chrome"))?.Id);
    }

    // ---------- reveal triggers / layout knobs ----------

    [TestMethod]
    public void RePeek_FiresOnlyAfterTheInterval_AndNeverWhileHidden()
    {
        var settings = new OverlaySettings { RePeekSeconds = 90 };

        Assert.IsFalse(IdlePolicy.RePeekDue(settings, hidden: false, secondsSinceReveal: 89));
        Assert.IsTrue(IdlePolicy.RePeekDue(settings, hidden: false, secondsSinceReveal: 90));
        Assert.IsFalse(IdlePolicy.RePeekDue(settings, hidden: true, secondsSinceReveal: 600));
    }

    [TestMethod]
    public void HiddenOverlay_KeepsAWindowChangePending_SoUnhidingShowsIt()
    {
        // Hide, switch window, un-hide. The switch must not be swallowed while hidden, or
        // the panel stays invisible until some unrelated trigger.
        var hiddenTick = RevealPolicy.ObserveWindow(lastWindowKey: 111, currentWindowKey: 222, hidden: true);
        Assert.IsFalse(hiddenTick.Changed);
        Assert.AreEqual(111, hiddenTick.WindowKey, "the change must stay pending");

        var afterUnhide = RevealPolicy.ObserveWindow(hiddenTick.WindowKey, 222, hidden: false);
        Assert.IsTrue(afterUnhide.Changed, "un-hiding should reveal the pending switch");
        Assert.AreEqual(222, afterUnhide.WindowKey);

        var nextTick = RevealPolicy.ObserveWindow(afterUnhide.WindowKey, 222, hidden: false);
        Assert.IsFalse(nextTick.Changed, "the switch must not fire twice");
    }

    [TestMethod]
    public void HiddenOverlay_KeepsAnAppChangePending_AndIgnoresTheSameApp()
    {
        Assert.IsFalse(RevealPolicy.ObserveAppChange("devin", "chatgpt", hidden: true));
        Assert.IsTrue(RevealPolicy.ObserveAppChange("devin", "chatgpt", hidden: false));
        Assert.IsFalse(RevealPolicy.ObserveAppChange("chrome", "chrome", hidden: false));
    }

    [TestMethod]
    public void RePeek_ZeroDisablesPeriodicReveals()
    {
        var settings = new OverlaySettings { RePeekSeconds = 0 };
        Assert.IsFalse(IdlePolicy.RePeekDue(settings, hidden: false, secondsSinceReveal: 10_000));
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
    public void Place_LeavesAnOnScreenPanelAlone_EvenIfItIsNotDocked()
    {
        var work = new Rect(0, 0, 1920, 1040);
        var floating = new Rect(500, 300, 460, 200);

        var p = OverlayGeometry.Place(floating, 1800, 1000, work);

        Assert.AreEqual(500, p.X);
        Assert.AreEqual(300, p.Y);
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
    public void SaveOverlay_PersistsSettings_WithoutSwallowingAppFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "apps"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "shortcuts.json"),
                """{"overlay":{"mode":"peek"},"apps":[{"id":"global","name":"General","shortcuts":[]}]}""");
            File.WriteAllText(Path.Combine(dir, "apps", "devin.json"),
                """{"id":"devin","name":"Devin","priority":1000,"shortcuts":[{"keys":"Ctrl+K","action":"Palette"}]}""");

            var store = new ConfigStore(dir);
            store.Config.Overlay.FontSize = 17;
            store.Config.Overlay.MaxRows = 5;
            store.SaveOverlay();

            // The app that lives in its own file must not be copied into shortcuts.json,
            // or it would shadow the real file on the next load.
            var main = File.ReadAllText(Path.Combine(dir, "shortcuts.json"));
            StringAssert.Contains(main, "\"font_size\": 17");
            Assert.IsFalse(main.Contains("Ctrl+K"), "SaveOverlay must not absorb per-app files");

            var reloaded = new ConfigStore(dir);
            Assert.AreEqual(17, reloaded.Config.Overlay.FontSize);
            Assert.AreEqual(5, reloaded.Config.Overlay.MaxRows);
            Assert.AreEqual("devin", reloaded.Config.Apps.Single(a => a.Id == "devin").Id);
            Assert.AreEqual(1, reloaded.Config.Apps.Single(a => a.Id == "devin").Shortcuts.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "config")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "could not locate the repo config folder");
        return dir!.FullName;
    }
}
