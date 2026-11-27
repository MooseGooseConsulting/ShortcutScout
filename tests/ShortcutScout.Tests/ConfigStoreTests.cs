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
        foreach (var id in new[] { "chrome", "claudecode", "vs-code", "app_2", "notepad++", "My App", "a.b" })
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
}

[TestClass]
public class ConfigLocatorTests
{
    [TestMethod]
    public void FindsTheRepoConfigByWalkingUpFromTheBuildOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-loc-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "src", "ShortcutScout", "bin", "Release", "net8.0-windows");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(root, "config", "apps"));
        try
        {
            Assert.AreEqual(
                Path.Combine(root, "config"),
                ConfigLocator.Find(new[] { bin }));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void IgnoresAnUnrelatedFolderThatHappensToBeCalledConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-loc-" + Guid.NewGuid().ToString("N"));
        var elsewhere = Path.Combine(root, "other-project");
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(Path.Combine(elsewhere, "config")); // no shortcuts.json, no apps/
        Directory.CreateDirectory(Path.Combine(install, "config"));
        File.WriteAllText(Path.Combine(install, "config", "shortcuts.json"), "{}");
        try
        {
            Assert.AreEqual(
                Path.Combine(install, "config"),
                ConfigLocator.Find(new[] { elsewhere, install }));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FallsBackToAConfigFolderBesideTheFirstCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-loc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Bounded by maxDepth, so a real config further up the temp tree can't be found.
            Assert.AreEqual(Path.Combine(root, "config"), ConfigLocator.Find(new[] { root }, maxDepth: 0));
        }
        finally { Directory.Delete(root, true); }
    }
}

[TestClass]
public class ConfigStoreWrittenTests
{
    [TestMethod]
    public void EveryWrite_IsAnnounced_SoTheLogCanSayWhatChanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-written-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shortcuts.json"),
                """{ "apps": [ { "id": "global", "name": "General", "shortcuts": [] } ] }""");
            var store = new ConfigStore(dir);
            var seen = new List<string>();
            store.Written += seen.Add;

            store.AddShortcut("myapp", new Shortcut("Ctrl+X", "X"));
            store.AddShortcut("global", new Shortcut("Win+V", "Clipboard"));
            store.SaveOverlay();
            store.AddShortcut("myapp", new Shortcut("Ctrl+X", "X")); // duplicate: nothing written
            store.RemoveShortcut("myapp", "no-such-keys");            // nothing removed: nothing written
            store.Reload();                                           // reading is not writing

            Assert.AreEqual(3, seen.Count, string.Join(" | ", seen));
            StringAssert.Contains(seen[0], "apps/myapp.json");
            StringAssert.Contains(seen[1], "app 'global'");
            StringAssert.Contains(seen[2], "overlay");
        }
        finally { Directory.Delete(dir, true); }
    }
}
