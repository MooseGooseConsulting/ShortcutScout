using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary><see cref="RevealPolicy"/>: window and app changes observed while hidden stay pending.</summary>
[TestClass]
public class RevealPolicyTests
{
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
}
