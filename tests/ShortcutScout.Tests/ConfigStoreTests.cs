using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary>
/// Disk behaviour of <see cref="ConfigStore"/>: what survives a bad file, and whether an
/// edit is still there after the next reload.
/// </summary>
[TestClass]
public class ConfigStoreTests
{
    private const string MainWithGlobal =
        """
        {
          "overlay": { "mode": "ambient", "peek_seconds": 9 },
          "apps": [
            { "id": "global", "name": "General", "priority": 0,
              "shortcuts": [ { "keys": "Alt+Tab", "action": "Switch window" } ] }
          ]
        }
        """;

    private static string NewDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ss-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "apps"));
        return dir;
    }

    private static string MainFile(string dir) => Path.Combine(dir, "shortcuts.json");

    [TestMethod]
    public void MalformedMainFile_DoesNotThrow_AndStillLoadsAppFiles()
    {
        var dir = NewDir("badmain");
        try
        {
            File.WriteAllText(MainFile(dir), "{ \"overlay\": { oops ");
            File.WriteAllText(Path.Combine(dir, "apps", "good.json"),
                """{"id":"good","name":"Good","shortcuts":[{"keys":"Ctrl+G","action":"Go"}]}""");

            var store = new ConfigStore(dir);

            Assert.AreEqual("good", store.Config.Apps.Single().Id);
            Assert.AreEqual(1, store.LoadErrors.Count, "the bad file is reported, not swallowed");
            StringAssert.Contains(store.LoadErrors[0], "shortcuts.json");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ReloadAfterMainFileBreaks_KeepsTheLastGoodSettings()
    {
        var dir = NewDir("regress");
        try
        {
            File.WriteAllText(MainFile(dir), MainWithGlobal);
            var store = new ConfigStore(dir);
            Assert.AreEqual(0, store.LoadErrors.Count);

            // A half-saved hand edit: the running overlay must not snap back to defaults.
            File.WriteAllText(MainFile(dir), "{ \"overlay\": ");
            store.Reload();

            Assert.AreEqual("ambient", store.Config.Overlay.Mode);
            Assert.AreEqual(9, store.Config.Overlay.PeekSeconds);
            Assert.IsTrue(store.Config.Apps.Any(a => a.Id == "global"), "inline apps survive too");
            Assert.AreEqual(1, store.LoadErrors.Count);

            File.WriteAllText(MainFile(dir), MainWithGlobal);
            store.Reload();
            Assert.AreEqual(0, store.LoadErrors.Count, "errors clear once the file is fixed");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void MalformedAppFile_IsReportedByName()
    {
        var dir = NewDir("badapp");
        try
        {
            File.WriteAllText(Path.Combine(dir, "apps", "broken.json"), "{ not json ");
            var store = new ConfigStore(dir);

            Assert.AreEqual(0, store.Config.Apps.Count);
            StringAssert.Contains(store.LoadErrors.Single(), "broken.json");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void NullListsAndBlankEntries_AreNormalisedInsteadOfCrashingLater()
    {
        var dir = NewDir("nulls");
        try
        {
            File.WriteAllText(MainFile(dir), """{ "overlay": null, "apps": null }""");
            File.WriteAllText(Path.Combine(dir, "apps", "sparse.json"),
                """{"name":"Sparse","process_names":null,"title_contains":null,"shortcuts":[null,{"keys":"","action":"blank"},{"keys":"Ctrl+1","action":"One"}]}""");

            var store = new ConfigStore(dir);
            var app = store.Config.Apps.Single();

            Assert.IsNotNull(store.Config.Overlay);
            Assert.AreEqual("sparse", app.Id, "a missing id falls back to the file name");
            Assert.IsNotNull(app.ProcessNames);
            Assert.IsNotNull(app.ExeNames);
            Assert.IsNotNull(app.TitleContains);
            Assert.AreEqual("Ctrl+1", app.Shortcuts.Single().Keys, "null and key-less rows are dropped");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void AddingToAnAppDefinedInTheMainFile_SurvivesAReload()
    {
        var dir = NewDir("inline-add");
        try
        {
            File.WriteAllText(MainFile(dir), MainWithGlobal);
            var store = new ConfigStore(dir);

            Assert.IsTrue(store.AddShortcut("global", new Shortcut("Win+V", "Clipboard history", Discovery: true)));

            // Before the fix this wrote apps/global.json, which the next load ignored because
            // shortcuts.json already defines "global" — the edit reported success and vanished.
            var reloaded = new ConfigStore(dir);
            var global = reloaded.Config.Apps.Single(a => a.Id == "global");
            Assert.AreEqual(2, global.Shortcuts.Count);
            Assert.IsTrue(global.Shortcuts.Any(s => s.Keys == "Win+V" && s.Discovery));

            Assert.IsFalse(File.Exists(Path.Combine(dir, "apps", "global.json")),
                "no shadow file that the loader would ignore");
            Assert.AreEqual("ambient", reloaded.Config.Overlay.Mode, "the overlay block is untouched");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void RemovingFromAnAppDefinedInTheMainFile_SurvivesAReload()
    {
        var dir = NewDir("inline-remove");
        try
        {
            File.WriteAllText(MainFile(dir), MainWithGlobal);
            var store = new ConfigStore(dir);

            Assert.IsTrue(store.RemoveShortcut("global", "Alt+Tab"));

            var reloaded = new ConfigStore(dir);
            Assert.AreEqual(0, reloaded.Config.Apps.Single(a => a.Id == "global").Shortcuts.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void SavedFiles_KeepKeysHumanReadable()
    {
        var dir = NewDir("encoding");
        try
        {
            File.WriteAllText(MainFile(dir), MainWithGlobal);
            var store = new ConfigStore(dir);
            store.AddShortcut("myapp", new Shortcut("Ctrl+Shift+<", "Shrink & tidy"));
            store.AddShortcut("global", new Shortcut("Win+.", "Emoji picker"));
            store.SaveOverlay();

            var appFile = File.ReadAllText(Path.Combine(dir, "apps", "myapp.json"));
            StringAssert.Contains(appFile, "\"Ctrl+Shift+<\"");
            StringAssert.Contains(appFile, "Shrink & tidy");

            var main = File.ReadAllText(MainFile(dir));
            StringAssert.Contains(main, "\"Alt+Tab\"");
            StringAssert.Contains(main, "\"Win+.\"");
            Assert.IsFalse(main.Contains("\\u002B"), "'+' must not be written as an escape");
        }
        finally { Directory.Delete(dir, true); }
    }

    [DataTestMethod]
    [DataRow("../escape")]
    [DataRow("..")]
    [DataRow("a/b")]
    [DataRow("a\\b")]
    [DataRow("C:evil")]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("con.")]
    [DataRow("con")]      // Windows device names: apps/con.json is the console, not a file
    [DataRow("NUL")]
    [DataRow("com1")]
    [DataRow("Lpt9")]
    [DataRow("aux.backup")]
    public void AppIdsThatAreNotPlainFileNames_AreRefused(string id)
    {
        var dir = NewDir("ids");
        try
        {
            var store = new ConfigStore(dir);

            Assert.IsFalse(ConfigStore.IsValidAppId(id));
            Assert.ThrowsException<ArgumentException>(() => store.AddShortcut(id, new Shortcut("Ctrl+X", "X")));
            Assert.AreEqual(0, Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length,
                "nothing may be written for a refused id");
            Assert.AreEqual(0, store.Config.Apps.Count, "and no phantom app is left in memory");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void OrdinaryAppIds_AreAccepted()
    {
        foreach (var id in new[] { "chrome", "claudecode", "vs-code", "app_2", "notepad++", "My App", "a.b",
                                   "console", "com10", "config", "null" })
            Assert.IsTrue(ConfigStore.IsValidAppId(id), id);
    }

    [TestMethod]
    public void AShortcutWithoutKeys_IsRefused()
    {
        var dir = NewDir("blank");
        try
        {
            var store = new ConfigStore(dir);
            Assert.ThrowsException<ArgumentException>(() => store.AddShortcut("myapp", new Shortcut("  ", "nothing")));
            Assert.AreEqual(0, store.Config.Apps.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Saving_LeavesNoTempFilesBehind()
    {
        var dir = NewDir("atomic");
        try
        {
            var store = new ConfigStore(dir);
            store.AddShortcut("myapp", new Shortcut("Ctrl+X", "X"));
            store.SaveOverlay();

            var stray = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.AreEqual(0, stray.Count, string.Join(", ", stray));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void SaveOverlay_RefusesToClobberAMainFileItCannotParse()
    {
        var dir = NewDir("noclobber");
        try
        {
            const string broken = "{ \"overlay\": { \"mode\": \"ambient\", ";
            File.WriteAllText(MainFile(dir), broken);
            var store = new ConfigStore(dir);

            // The concrete type is an internal subclass, so match JsonException by inheritance.
            try { store.SaveOverlay(); Assert.Fail("expected the save to be refused"); }
            catch (System.Text.Json.JsonException) { }
            Assert.AreEqual(broken, File.ReadAllText(MainFile(dir)), "a hand edit in progress is left alone");
        }
        finally { Directory.Delete(dir, true); }
    }

    [DataTestMethod]
    // Valid JSON that Reload rejects. The store is holding defaults, so writing its overlay
    // back would replace the very block the user still has to repair.
    [DataRow("""[ { "overlay": { "mode": "ambient" } } ]""")]
    [DataRow("""{ "overlay": { "mode": "ambient", "width": "460" } }""")]
    [DataRow("""{ "overlay": { "mode": "ambient" }, "apps": { "id": "global" } }""")]
    [DataRow("null")]
    public void SaveOverlay_RefusesToClobberAMainFileThatParsesButDoesNotLoad(string unloadable)
    {
        var dir = NewDir("noclobber2");
        try
        {
            File.WriteAllText(MainFile(dir), unloadable);
            var store = new ConfigStore(dir);

            try { store.SaveOverlay(); Assert.Fail("expected the save to be refused"); }
            catch (System.Text.Json.JsonException) { }
            Assert.AreEqual(unloadable, File.ReadAllText(MainFile(dir)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void AnEdit_KeepsShortcutsAddedByHandSinceTheStoreLoaded()
    {
        var dir = NewDir("stale");
        try
        {
            File.WriteAllText(MainFile(dir), MainWithGlobal);
            File.WriteAllText(Path.Combine(dir, "apps", "myapp.json"),
                """{"id":"myapp","shortcuts":[{"keys":"Ctrl+1","action":"One"}]}""");
            var store = new ConfigStore(dir); // a long-lived MCP server's view

            // Hand edits behind its back, to an inline app and to an app file.
            File.WriteAllText(MainFile(dir), MainWithGlobal.Replace(
                """[ { "keys": "Alt+Tab", "action": "Switch window" } ]""",
                """[ { "keys": "Alt+Tab", "action": "Switch window" }, { "keys": "Win+V", "action": "Clipboard" } ]"""));
            File.WriteAllText(Path.Combine(dir, "apps", "myapp.json"),
                """{"id":"myapp","shortcuts":[{"keys":"Ctrl+1","action":"One"},{"keys":"Ctrl+2","action":"Two"}]}""");

            Assert.IsTrue(store.AddShortcut("global", new Shortcut("Win+R", "Run")));
            Assert.IsTrue(store.RemoveShortcut("myapp", "Ctrl+1"));

            var reloaded = new ConfigStore(dir);
            CollectionAssert.AreEqual(new[] { "Alt+Tab", "Win+V", "Win+R" },
                reloaded.Config.Apps.Single(a => a.Id == "global").Shortcuts.Select(s => s.Keys).ToArray());
            CollectionAssert.AreEqual(new[] { "Ctrl+2" },
                reloaded.Config.Apps.Single(a => a.Id == "myapp").Shortcuts.Select(s => s.Keys).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void AnEditThatCouldNotBeSaved_CanBeRetriedOnceTheFileIsRepaired()
    {
        var dir = NewDir("retry");
        try
        {
            File.WriteAllText(MainFile(dir), MainWithGlobal);
            var store = new ConfigStore(dir);

            File.WriteAllText(MainFile(dir), "{ \"overlay\": ");
            try { store.AddShortcut("global", new Shortcut("Win+V", "Clipboard")); Assert.Fail("expected the save to fail"); }
            catch (System.Text.Json.JsonException) { }
            try { store.RemoveShortcut("global", "Alt+Tab"); Assert.Fail("expected the save to fail"); }
            catch (System.Text.Json.JsonException) { }

            CollectionAssert.AreEqual(new[] { "Alt+Tab" },
                store.Config.Apps.Single(a => a.Id == "global").Shortcuts.Select(s => s.Keys).ToArray(),
                "an edit that never reached the disk must not linger in memory");

            // Lingering in memory, the add would now answer "already exists" and never be written.
            File.WriteAllText(MainFile(dir), MainWithGlobal);
            Assert.IsTrue(store.AddShortcut("global", new Shortcut("Win+V", "Clipboard")));
            Assert.AreEqual(2, new ConfigStore(dir).Config.Apps.Single(a => a.Id == "global").Shortcuts.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void WhileTheMainFileIsUnreadable_NoNewAppFileIsCreated()
    {
        var dir = NewDir("unknown-inline");
        try
        {
            // The first load fails, so the store cannot know that "global" lives in this file.
            File.WriteAllText(MainFile(dir), MainWithGlobal.TrimEnd().TrimEnd('}'));
            File.WriteAllText(Path.Combine(dir, "apps", "myapp.json"),
                """{"id":"myapp","shortcuts":[{"keys":"Ctrl+1","action":"One"}]}""");
            var store = new ConfigStore(dir);
            Assert.AreEqual(1, store.LoadErrors.Count);

            // apps/global.json would be ignored as a duplicate as soon as the brace is restored.
            Assert.ThrowsException<IOException>(() => store.AddShortcut("global", new Shortcut("Win+V", "Clipboard")));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "apps", "global.json")));

            // An app that was loaded from apps/ is known to live there and stays editable.
            Assert.IsTrue(store.AddShortcut("myapp", new Shortcut("Ctrl+2", "Two")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
