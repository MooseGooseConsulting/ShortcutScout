namespace ShortcutScout.Core;

/// <summary>
/// Gently rotates lesser-known (discovery) shortcuts into view over time.
///
/// The selection only changes once per <see cref="Slot"/>, not on every UI tick,
/// so the panel is stable to read and then quietly reveals something new.
/// </summary>
public sealed class Rotation
{
    private readonly HashSet<string> _seen = new();

    /// <summary>How long one selection stays on screen before it gently rotates.</summary>
    public static readonly TimeSpan Slot = TimeSpan.FromMinutes(3);

    /// <summary>Convert elapsed time into a rotation slot index (stable between rotations).</summary>
    public static long SlotFor(TimeSpan elapsed) =>
        (long)(elapsed.TotalSeconds / Slot.TotalSeconds);

    public IReadOnlyCollection<string> Seen => _seen;

    /// <summary>
    /// Choose up to <paramref name="maxItems"/> shortcuts: core entries first,
    /// interleaved with discovery entries that have not been shown yet.
    /// </summary>
    public List<Shortcut> Select(IReadOnlyList<Shortcut> all, int maxItems, long slot)
    {
        if (all.Count == 0 || maxItems <= 0) return new();

        var core = all.Where(s => !s.Discovery).ToList();
        var discovery = all.Where(s => s.Discovery).ToList();
        var rotated = Rotate(discovery, slot);

        // Prefer discovery entries we have not surfaced yet.
        var unseen = rotated.Where(s => !_seen.Contains(Key(s))).ToList();
        var queue = unseen.Concat(rotated.Where(s => _seen.Contains(Key(s)))).ToList();

        var result = new List<Shortcut>();
        int ci = 0, di = 0;
        while (result.Count < maxItems)
        {
            var added = false;
            for (int i = 0; i < 2 && ci < core.Count && result.Count < maxItems; i++)
            {
                if (!result.Contains(core[ci])) result.Add(core[ci]);
                ci++;
                added = true;
            }
            if (di < queue.Count && result.Count < maxItems)
            {
                if (!result.Contains(queue[di])) result.Add(queue[di]);
                di++;
                added = true;
            }
            if (!added) break; // nothing left to place
        }

        foreach (var s in result) _seen.Add(Key(s));
        return result;
    }

    private static List<Shortcut> Rotate(List<Shortcut> items, long slot)
    {
        if (items.Count == 0) return new();
        var offset = (int)(((slot % items.Count) + items.Count) % items.Count);
        return items.Skip(offset).Concat(items.Take(offset)).ToList();
    }

    private static string Key(Shortcut s) => s.Keys + "|" + s.Action;
}
