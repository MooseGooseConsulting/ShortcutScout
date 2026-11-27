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

        // The overlay asks again on every 500 ms tick. Selecting marks rows as seen, so
        // recomputing gave a different "unseen first" order on the second call and the rows
        // reshuffled half a second into an appearance. One request, one answer: the same
        // list, row count and slot return the selection already made.
        var signature = string.Join("\n", all.Select(s => Key(s) + (s.Discovery ? "|d" : "")));
        if (_last is { } last && last.Slot == slot && last.MaxItems == maxItems && last.Signature == signature)
            return new List<Shortcut>(last.Rows);

        var selected = Compute(all, maxItems, slot);
        _last = (signature, maxItems, slot, selected);
        return new List<Shortcut>(selected);
    }

    private (string Signature, int MaxItems, long Slot, List<Shortcut> Rows)? _last;

    private List<Shortcut> Compute(IReadOnlyList<Shortcut> all, int maxItems, long slot)
    {
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
