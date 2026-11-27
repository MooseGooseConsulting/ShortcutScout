using System.IO;
using ShortcutScout.Core;

namespace ShortcutScout;

/// <summary>
/// Where the app writes about itself.
///
/// <list type="bullet">
/// <item><b>Event log</b> — always on, small: startup, every reveal with its reason, config
/// problems, hotkey failures, crashes. <c>%LOCALAPPDATA%\ShortcutScout\logs\shortcutscout.log</c>,
/// 1 MB plus one backup.</item>
/// <item><b>Trace</b> — only with <c>SHORTCUTSCOUT_TRACE=1</c>: cursor/placement/opacity on every
/// 500 ms tick, for tuning. <c>%TEMP%\shortcutscout-trace.log</c>, 20 MB plus one backup (it was
/// unbounded). Events are mirrored into it so one file tells the whole story.</item>
/// </list>
/// </summary>
internal static class AppLog
{
    public static readonly bool Tracing =
        Environment.GetEnvironmentVariable("SHORTCUTSCOUT_TRACE") == "1";

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShortcutScout", "logs");

    public static string EventLogPath { get; } = Path.Combine(Folder, "shortcutscout.log");
    public static string TracePath { get; } = Path.Combine(Path.GetTempPath(), "shortcutscout-trace.log");

    private static readonly FileLog Events = new(EventLogPath, maxBytes: 1_000_000);

    // Time-only stamps: the trace is a short-lived tuning aid, and its line format is what the
    // README's examples and existing greps expect.
    private static readonly FileLog TraceFile = new(TracePath, maxBytes: 20_000_000, timestampFormat: "HH:mm:ss.fff");

    public static void Info(string message)
    {
        Events.Info(message);
        if (Tracing) TraceFile.Write("", message);
    }

    public static void Warn(string message)
    {
        Events.Warn(message);
        if (Tracing) TraceFile.Write("", "WARN " + message);
    }

    public static void Error(string message, Exception? ex = null)
    {
        Events.Error(message, ex);
        if (Tracing) TraceFile.Write("", "ERROR " + message + (ex == null ? "" : " — " + ex.Message));
    }

    /// <summary>Per-tick detail. A no-op unless tracing, so callers needn't guard cheap messages.</summary>
    public static void Trace(string message)
    {
        if (Tracing) TraceFile.Write("", message);
    }
}
