using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary>Helpers shared by the test classes (import with <c>using static</c>).</summary>
internal static class TestData
{
    internal static AppConfig App(string id, int priority, List<string> procs, List<string> titles, params Shortcut[] shortcuts) => new()
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
    internal static List<AppConfig> Sample() => new()
    {
        App("global", 0, new(), new()),
        App("chatgpt", 1000, new(), new() { "chatgpt.com", "ChatGPT" }, new Shortcut("Ctrl+Enter", "Send")),
        App("devin", 1000, new(), new() { "devin.ai", "Devin" }, new Shortcut("Ctrl+K", "Palette", Discovery: true), new Shortcut("Esc", "Stop")),
        App("chrome", 10, new() { "chrome" }, new() { "Google Chrome" }, new Shortcut("Ctrl+T", "New tab")),
        App("zellij", 800, new() { "zellij" }, new() { "zellij" }, new Shortcut("Ctrl+g", "Lock")),
    };

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "config")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "could not locate the repo config folder");
        return dir!.FullName;
    }
}
