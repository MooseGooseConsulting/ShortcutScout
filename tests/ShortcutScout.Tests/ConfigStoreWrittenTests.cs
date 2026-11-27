using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary>Every config write is announced, so the event log can say who changed what.</summary>
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
