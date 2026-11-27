using System.Text;

namespace ShortcutScout.Core;

/// <summary>
/// A size-capped append-only text log: one current file plus one backup (<c>name.log.1</c>).
///
/// Deliberately tiny. The overlay is a single-user tray tool, so what it needs from a log is
/// "what happened before it stopped appearing" a week later — not levels of indirection.
/// Writing never throws: a full disk or a locked file must not take the overlay down, and a
/// log that cannot be written is simply skipped.
/// </summary>
public sealed class FileLog
{
    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private readonly string _timestampFormat;
    private long _size = -1; // unknown until the first write
    private readonly Mutex _crossProcess;

    // No byte-order mark: it shows up as a stray character in `type`, `cat` and grep output.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public string FilePath { get; }
    public string BackupPath => FilePath + ".1";

    /// <summary>The file is rolled over before a write would take it past this.</summary>
    public long MaxBytes { get; }

    public FileLog(string filePath, long maxBytes = 1_000_000,
        string timestampFormat = "yyyy-MM-dd HH:mm:ss.fff", Func<DateTime>? now = null)
    {
        FilePath = filePath;
        MaxBytes = Math.Max(1024, maxBytes);
        _timestampFormat = timestampFormat;
        _now = now ?? (() => DateTime.Now);

        // Named after the file, so every process logging to it shares the one mutex.
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(filePath).ToUpperInvariant())))[..16];
        _crossProcess = new Mutex(false, @"Local\ShortcutScout.Log." + key);
    }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

    /// <summary>Append one entry. <paramref name="level"/> may be empty for a bare line.</summary>
    public void Write(string level, string message)
    {
        try
        {
            var line = level.Length == 0
                ? $"{_now().ToString(_timestampFormat)} {message}{Environment.NewLine}"
                : $"{_now().ToString(_timestampFormat)} {level} {message}{Environment.NewLine}";
            var bytes = Encoding.UTF8.GetByteCount(line);

            lock (_gate)
            {
                // The overlay and a `--mcp` / `--cli` process log to the same file. `_gate`
                // only covers this process, so measuring, rolling over and appending also
                // happen under a named mutex — otherwise two processes open at the same end
                // of file and one entry lands on top of the other.
                var held = false;
                try
                {
                    try { held = _crossProcess.WaitOne(TimeSpan.FromMilliseconds(500)); }
                    catch (AbandonedMutexException) { held = true; } // the other process died mid-write
                    if (!held) return; // skip the line rather than stall the overlay

                    if (_size < 0)
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);

                    // Measured every time: another process may have appended or rolled it over.
                    _size = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
                    if (_size > 0 && _size + bytes > MaxBytes)
                    {
                        File.Move(FilePath, BackupPath, overwrite: true);
                        _size = 0;
                    }

                    using (var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        var data = Utf8NoBom.GetBytes(line);
                        stream.Write(data, 0, data.Length);
                    }
                    _size += bytes;
                }
                finally
                {
                    if (held) _crossProcess.ReleaseMutex();
                }
            }
        }
        catch
        {
            // Logging must never break the thing it is describing.
            _size = -1; // re-measure next time; the file may have been moved or deleted
        }
    }
}
