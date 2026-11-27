using ShortcutScout.Core;
using static ShortcutScout.Tests.TestData;

namespace ShortcutScout.Tests;

/// <summary>The config files shipped in the repo, loaded from disk.</summary>
[TestClass]
public class ShippedConfigTests
{
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
}
