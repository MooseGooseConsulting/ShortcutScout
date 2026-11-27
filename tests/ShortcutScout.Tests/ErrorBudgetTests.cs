using ShortcutScout.Core;

namespace ShortcutScout.Tests;

[TestClass]
public class ErrorBudgetTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [TestMethod]
    public void AnOccasionalError_IsAbsorbedIndefinitely()
    {
        var budget = new ErrorBudget(maxErrors: 5, window: TimeSpan.FromMinutes(1));

        // One failure every 20 s for an hour: never more than three inside any minute.
        for (var t = 0.0; t < 3600; t += 20)
            Assert.IsTrue(budget.Absorb(At(t)), $"t={t}");
    }

    [TestMethod]
    public void TheSameFailureOnEveryTick_ExhaustsTheBudgetWithinSeconds()
    {
        var budget = new ErrorBudget(maxErrors: 5, window: TimeSpan.FromMinutes(1));

        // The 500 ms timer throwing each time.
        Assert.IsTrue(budget.Absorb(At(0.0)));
        Assert.IsTrue(budget.Absorb(At(0.5)));
        Assert.IsTrue(budget.Absorb(At(1.0)));
        Assert.IsTrue(budget.Absorb(At(1.5)));
        Assert.IsFalse(budget.Absorb(At(2.0)), "the fifth in a row means it is not transient");
    }

    [TestMethod]
    public void OldErrors_FallOutOfTheWindow()
    {
        var budget = new ErrorBudget(maxErrors: 3, window: TimeSpan.FromSeconds(10));

        Assert.IsTrue(budget.Absorb(At(0)));
        Assert.IsTrue(budget.Absorb(At(1)));
        Assert.IsTrue(budget.Absorb(At(12)), "the first two have aged out");
        Assert.IsTrue(budget.Absorb(At(13)));
        Assert.IsFalse(budget.Absorb(At(14)));
    }
}
