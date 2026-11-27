using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary><see cref="ConfigStore"/> basics: loading overlay settings and app files, add/remove, saving.</summary>
[TestClass]
public class ConfigTests
{
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
}
