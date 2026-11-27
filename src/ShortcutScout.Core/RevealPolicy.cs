namespace ShortcutScout.Core;

/// <summary>What the last observation of the foreground window means for revealing.</summary>
public readonly record struct TriggerState(bool Changed, long WindowKey);

public static class RevealPolicy
{
    /// <summary>
    /// Observe the foreground window and decide whether that counts as a change.
    ///
    /// While the panel is explicitly hidden (Ctrl+Alt+S) the change is *not consumed*: the
    /// returned key is the old one, so the switch stays pending. Otherwise un-hiding would sit
    /// on a stale key, and the panel could remain invisible until some unrelated trigger —
    /// indefinitely, if periodic reveals are disabled.
    /// </summary>
    public static TriggerState ObserveWindow(long lastWindowKey, long currentWindowKey, bool hidden)
        => hidden || currentWindowKey == NoWindow
            ? new TriggerState(false, lastWindowKey)
            : new TriggerState(currentWindowKey != lastWindowKey, currentWindowKey);

    /// <summary>
    /// What <c>GetForegroundWindow</c> returns while focus is in transit between windows, and
    /// on the lock screen. It is not a window the user switched *to*: treating it as one fired
    /// a peek for the fallback app mid-alt-tab, and the anti-strobe gap then delayed the peek
    /// for the window that actually arrived.
    /// </summary>
    public const long NoWindow = 0;

    /// <summary>Same rule for the matched app, which changes on tab switches in one window.</summary>
    public static bool ObserveAppChange(string lastAppId, string currentAppId, bool hidden)
        => !hidden && !string.Equals(lastAppId, currentAppId, StringComparison.Ordinal);
}
