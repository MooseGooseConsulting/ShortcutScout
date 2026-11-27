using ShortcutScout.Core;

namespace ShortcutScout.Tests;

[TestClass]
public class FileLogTests
{
    private string _dir = "";

    [TestInitialize]
    public void Setup() => _dir = Path.Combine(Path.GetTempPath(), "ss-log-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static readonly DateTime Fixed = new(2026, 11, 27, 9, 30, 15, 250);

    [TestMethod]
    public void CreatesItsFolder_AndStampsEachLineWithTimeAndLevel()
    {
        var log = new FileLog(Path.Combine(_dir, "nested", "app.log"), now: () => Fixed);

        log.Info("started");
        log.Warn("hotkey taken");

        var lines = File.ReadAllLines(log.FilePath);
        Assert.AreEqual("2026-11-27 09:30:15.250 INFO  started", lines[0]);
        Assert.AreEqual("2026-11-27 09:30:15.250 WARN  hotkey taken", lines[1]);
    }

    [TestMethod]
    public void AnError_CarriesTheExceptionTypeMessageAndStack()
    {
        var log = new FileLog(Path.Combine(_dir, "app.log"), now: () => Fixed);

        try { throw new InvalidOperationException("tick blew up"); }
        catch (Exception ex) { log.Error("unhandled exception on the UI thread", ex); }

        var text = File.ReadAllText(log.FilePath);
        StringAssert.Contains(text, "ERROR unhandled exception on the UI thread");
        StringAssert.Contains(text, "System.InvalidOperationException: tick blew up");
        StringAssert.Contains(text, nameof(AnError_CarriesTheExceptionTypeMessageAndStack), "the stack names where it happened");
    }

    [TestMethod]
    public void NeverGrowsPastItsCap_AndKeepsExactlyOneBackup()
    {
        var log = new FileLog(Path.Combine(_dir, "app.log"), maxBytes: 2048, now: () => Fixed);

        for (var i = 0; i < 400; i++) log.Info($"line {i:000} " + new string('x', 40));

        Assert.IsTrue(new FileInfo(log.FilePath).Length <= 2048, "current file is within the cap");
        Assert.IsTrue(new FileInfo(log.BackupPath).Length <= 2048, "so is the backup");
        Assert.AreEqual(2, Directory.GetFiles(_dir).Length, "no .2, .3, ... pile up");

        // Nothing recent is lost: the newest lines are contiguous across backup + current.
        var kept = File.ReadAllLines(log.BackupPath).Concat(File.ReadAllLines(log.FilePath)).ToList();
        StringAssert.Contains(kept[^1], "line 399");
        for (var i = 1; i < kept.Count; i++)
            Assert.AreEqual(399 - (kept.Count - 1 - i), int.Parse(kept[i].Split("line ")[1][..3]));
    }

    [TestMethod]
    public void PicksUpTheSizeOfAnExistingFile_SoTheCapSurvivesRestarts()
    {
        var path = Path.Combine(_dir, "app.log");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, new string('o', 2000));

        new FileLog(path, maxBytes: 2048, now: () => Fixed).Info("first line of a new session " + new string('x', 60));

        Assert.IsTrue(File.Exists(path + ".1"), "the old session's file was rolled, not appended past the cap");
        Assert.AreEqual(1, File.ReadAllLines(path).Length);
    }

    [TestMethod]
    public void AnUnwritablePath_IsSwallowed_BecauseLoggingMustNeverCrashTheApp()
    {
        // A directory where the file should be: every write fails.
        var path = Path.Combine(_dir, "app.log");
        Directory.CreateDirectory(path);
        var log = new FileLog(path);

        log.Info("nobody will read this");
        log.Error("nor this", new Exception("x"));
    }

    [TestMethod]
    public void RecoversAfterTheFileIsDeletedUnderneathIt()
    {
        var log = new FileLog(Path.Combine(_dir, "app.log"), now: () => Fixed);
        log.Info("one");
        Directory.Delete(_dir, true); // e.g. the user clears the log folder while the app runs

        log.Info("two");
        log.Info("three");

        StringAssert.Contains(File.ReadAllText(log.FilePath), "three");
    }

    [TestMethod]
    public void ConcurrentWriters_DoNotInterleaveOrDropLines()
    {
        var log = new FileLog(Path.Combine(_dir, "app.log"), maxBytes: 10_000_000, now: () => Fixed);

        Parallel.For(0, 200, i => log.Info($"entry-{i:000}"));

        var lines = File.ReadAllLines(log.FilePath);
        Assert.AreEqual(200, lines.Length);
        Assert.AreEqual(200, lines.Select(l => l.Split("entry-")[1]).Distinct().Count());
    }
}
