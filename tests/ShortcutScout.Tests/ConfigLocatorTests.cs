using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary>Where the config folder is found: beside the exe, up the tree, or a fallback.</summary>
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
