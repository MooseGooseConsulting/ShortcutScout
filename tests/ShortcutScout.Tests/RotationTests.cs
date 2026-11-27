using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary>
/// <see cref="Rotation"/> as the overlay uses it: ONE instance, asked again on every 500 ms
/// tick. The older rotation tests compare fresh instances and cannot see state carried
/// between calls.
/// </summary>
[TestClass]
public class RotationTests
{
    /// <summary>Five core + five discovery rows, the shape of the shipped Chrome config.</summary>
    private static List<Shortcut> ChromeLike() =>
        Enumerable.Range(0, 5).Select(i => new Shortcut($"C{i}", $"core {i}"))
            .Concat(Enumerable.Range(0, 5).Select(i => new Shortcut($"D{i}", $"discovery {i}", Discovery: true)))
            .ToList();

    private static string Keys(IEnumerable<Shortcut> rows) => string.Join(",", rows.Select(r => r.Keys));

    [TestMethod]
    public void AskingAgainForTheSameSlot_ReturnsTheSameRows()
    {
        var rotation = new Rotation();
        var all = ChromeLike();

        var first = Keys(rotation.Select(all, 8, slot: 1));

        // The overlay re-selects on every tick. Marking rows "seen" on the first call must not
        // reshuffle the second: that changed the rows 500 ms and again 1 s into an appearance.
        for (var tick = 0; tick < 6; tick++)
            Assert.AreEqual(first, Keys(rotation.Select(all, 8, slot: 1)), $"tick {tick}");
    }

    [TestMethod]
    public void TheNextSlot_PrefersDiscoveryRowsNotShownYet()
    {
        var rotation = new Rotation();
        var all = ChromeLike();

        var first = rotation.Select(all, 8, slot: 1).Where(s => s.Discovery).Select(s => s.Keys).ToList();
        var second = rotation.Select(all, 8, slot: 2).Where(s => s.Discovery).Select(s => s.Keys).ToList();

        Assert.AreEqual(3, first.Count);
        var fresh = all.Where(s => s.Discovery).Select(s => s.Keys).Except(first).ToList();
        CollectionAssert.IsSubsetOf(fresh, second, "the two never-shown rows lead the next appearance");
    }

    [TestMethod]
    public void EveryDiscoveryRow_IsSurfacedWithinAFewAppearances()
    {
        var rotation = new Rotation();
        var all = ChromeLike();

        for (long slot = 1; slot <= 3; slot++)
            for (var tick = 0; tick < 4; tick++) rotation.Select(all, 8, slot);

        foreach (var s in all.Where(s => s.Discovery))
            Assert.IsTrue(rotation.Seen.Contains(s.Keys + "|" + s.Action), s.Keys);
    }

    [TestMethod]
    public void ADifferentListOrRowCount_IsNotAnsweredFromTheLastSelection()
    {
        var rotation = new Rotation();
        var chrome = ChromeLike();
        var other = new List<Shortcut> { new("X1", "one"), new("X2", "two", Discovery: true) };

        rotation.Select(chrome, 8, slot: 4);

        Assert.AreEqual("X1,X2", Keys(rotation.Select(other, 8, slot: 4)), "another app at the same slot");
        Assert.AreEqual(4, rotation.Select(chrome, 4, slot: 4).Count, "max_rows changed in settings");

        var edited = chrome.Append(new Shortcut("C9", "added by an agent")).ToList();
        Assert.AreNotEqual(Keys(rotation.Select(chrome, 20, slot: 4)), Keys(rotation.Select(edited, 20, slot: 4)),
            "a reloaded config with a new shortcut");
    }

    [TestMethod]
    public void ListsThatOnlyLookAlikeWhenJoined_AreNotAnsweredFromTheLastSelection()
    {
        var rotation = new Rotation();

        // Both flatten to "a|b|c" under a keys|action key.
        rotation.Select(new List<Shortcut> { new("a|b", "c") }, 8, slot: 2);
        var moved = rotation.Select(new List<Shortcut> { new("a", "b|c") }, 8, slot: 2).Single();
        Assert.AreEqual("a", moved.Keys);

        // Category is part of a row too, though nothing draws it yet.
        rotation.Select(new List<Shortcut> { new("Ctrl+K", "Palette", Category: "General") }, 8, slot: 2);
        var recategorised = rotation.Select(new List<Shortcut> { new("Ctrl+K", "Palette", Category: "Search") }, 8, slot: 2).Single();
        Assert.AreEqual("Search", recategorised.Category);
    }
}
