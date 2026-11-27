using ShortcutScout.Core;
using static ShortcutScout.Tests.TestData;

namespace ShortcutScout.Tests;

/// <summary>Which app config wins for a given foreground window.</summary>
[TestClass]
public class MatcherTests
{
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
}
