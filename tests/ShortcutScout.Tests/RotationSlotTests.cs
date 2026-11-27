using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary><see cref="Rotation"/> selection per slot, each call on a fresh instance (see RotationTests for carried state).</summary>
[TestClass]
public class RotationSlotTests
{
    [TestMethod]
    public void Rotation_IsStableWithinASlot()
    {
        var all = Enumerable.Range(0, 12)
            .Select(i => new Shortcut($"K{i}", $"action {i}", Discovery: i % 2 == 0))
            .ToList();

        var slot = Rotation.SlotFor(TimeSpan.FromMinutes(3.5));
        var first = new Rotation().Select(all, 6, slot);
        var again = new Rotation().Select(all, 6, slot);

        CollectionAssert.AreEqual(
            first.Select(s => s.Keys).ToList(),
            again.Select(s => s.Keys).ToList(),
            "selection must not change between UI ticks inside one slot");
    }

    [TestMethod]
    public void Rotation_ChangesAcrossSlots()
    {
        var all = Enumerable.Range(0, 12)
            .Select(i => new Shortcut($"K{i}", $"action {i}", Discovery: i % 2 == 0))
            .ToList();

        var a = new Rotation().Select(all, 6, Rotation.SlotFor(TimeSpan.Zero));
        var b = new Rotation().Select(all, 6, Rotation.SlotFor(TimeSpan.FromMinutes(3)));

        Assert.AreNotEqual(
            string.Join(",", a.Select(s => s.Keys)),
            string.Join(",", b.Select(s => s.Keys)),
            "the list should gently change when the slot advances");
    }

    [TestMethod]
    public void Rotation_SlotAdvancesOnlyEveryThreeMinutes()
    {
        Assert.AreEqual(0, Rotation.SlotFor(TimeSpan.FromSeconds(0)));
        Assert.AreEqual(0, Rotation.SlotFor(TimeSpan.FromSeconds(179)));
        Assert.AreEqual(1, Rotation.SlotFor(TimeSpan.FromSeconds(180)));
        Assert.AreEqual(1, Rotation.SlotFor(TimeSpan.FromSeconds(359)));
        Assert.AreEqual(2, Rotation.SlotFor(TimeSpan.FromSeconds(360)));
    }

    [TestMethod]
    public void Rotation_SurfacesDiscoveryItems()
    {
        var all = new List<Shortcut>
        {
            new("A", "a"), new("B", "b"), new("C", "c"),
            new("D", "d", Discovery: true), new("E", "e", Discovery: true),
        };
        var picked = new Rotation().Select(all, 6, 0);

        Assert.AreEqual(5, picked.Count, "all five fit");
        Assert.IsTrue(picked.Any(s => s.Discovery), "discovery items must appear");
    }

    [TestMethod]
    public void Rotation_NeverExceedsMaxOrRepeatsAnEntry()
    {
        var all = Enumerable.Range(0, 9).Select(i => new Shortcut($"K{i}", $"a{i}", Discovery: i > 4)).ToList();
        var picked = new Rotation().Select(all, 4, 0);

        Assert.AreEqual(4, picked.Count);
        Assert.AreEqual(4, picked.Distinct().Count(), "no duplicate rows");
    }

    [TestMethod]
    public void Rotation_HandlesEmptyAndZeroMax()
    {
        Assert.AreEqual(0, new Rotation().Select(new List<Shortcut>(), 5, 0).Count);
        var one = new List<Shortcut> { new("A", "a") };
        Assert.AreEqual(0, new Rotation().Select(one, 0, 0).Count);
        Assert.AreEqual(1, new Rotation().Select(one, 5, 0).Count);
    }
}
