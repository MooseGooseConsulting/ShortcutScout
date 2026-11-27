namespace ShortcutScout.Core;

/// <summary>
/// How many unexpected errors to shrug off before giving up.
///
/// The overlay runs on a 500 ms timer. One bad tick (a window that vanished between two
/// Win32 calls, say) should be logged and survived — but an error that recurs on every tick
/// would otherwise be "handled" twice a second forever, filling the log behind a panel that
/// is quietly broken. A handful within a short window means it is not transient: stop.
/// </summary>
public sealed class ErrorBudget
{
    private readonly Queue<TimeSpan> _recent = new();

    public int MaxErrors { get; }
    public TimeSpan Window { get; }

    public ErrorBudget(int maxErrors = 5, TimeSpan? window = null)
    {
        MaxErrors = Math.Max(1, maxErrors);
        Window = window ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Record an error at <paramref name="now"/> (any monotonic clock). Returns true while
    /// the app should carry on, false once <see cref="MaxErrors"/> have landed inside
    /// <see cref="Window"/>.
    /// </summary>
    public bool Absorb(TimeSpan now)
    {
        while (_recent.Count > 0 && now - _recent.Peek() > Window) _recent.Dequeue();
        _recent.Enqueue(now);
        return _recent.Count < MaxErrors;
    }
}
