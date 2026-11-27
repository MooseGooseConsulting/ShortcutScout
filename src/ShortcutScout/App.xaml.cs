using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using ShortcutScout.Core;
using WinForms = System.Windows.Forms;
using Shortcut = ShortcutScout.Core.Shortcut;

namespace ShortcutScout;

public partial class App : System.Windows.Application
{
    private MainWindow? _win;
    private SettingsWindow? _settings;
    private readonly ConfigStore _store = new(FindConfig());
    private readonly ToggleState _toggle = new();
    private readonly Rotation _rotation = new();
    private DispatcherTimer? _timer;
    private WinForms.NotifyIcon? _tray;
    private IntPtr _hwnd;
    private long _tick;
    private Mutex? _singleInstance;
    private FileSystemWatcher? _configWatcher;
    private DispatcherTimer? _reloadDebounce;
    private readonly ErrorBudget _errorBudget = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    /// <summary>The overlay, tray and timer are up: from here an error can be survived.</summary>
    private bool _started;

    // Beside the exe first (an installed copy), walking up to the repo's config/ for a dev
    // build; the working directory is only a fallback, since a Start-menu or startup-folder
    // launch runs from wherever the shell happens to be.
    private static string FindConfig() =>
        ConfigLocator.Find(new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() });

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallCrashLogging();

        // Every writer goes through the store, in whichever process it runs: the settings
        // window here, or an agent/CLI in a separate `--mcp` / `--cli` process.
        var writer = e.Args.Contains("--mcp") ? "mcp" : e.Args.Contains("--cli") ? "cli" : "settings";
        _store.Written += file => AppLog.Info($"config written by {writer}: {file}");

        if (e.Args.Contains("--mcp")) { RunMcp(); Shutdown(); return; }
        if (e.Args.Contains("--sample")) { RunSample(e.Args); Shutdown(); return; }
        if (e.Args.Contains("--cli")) { RunCli(e.Args); Shutdown(); return; }

        // One overlay per session. A second copy would draw a second panel and fail to
        // register both hotkeys — which looked like "the hotkey stopped working".
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\ShortcutScout.Overlay", out var first);
        if (!first)
        {
            AppLog.Warn("another ShortcutScout overlay is already running in this session; exiting");
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        var version = typeof(App).Assembly.GetName().Version;
        AppLog.Info($"starting v{version} pid={Environment.ProcessId} config='{_store.ConfigDir}' " +
                    $"apps={_store.Config.Apps.Count} mode={_store.Config.Overlay.Mode} " +
                    $"trace={(AppLog.Tracing ? AppLog.TracePath : "off")}");

        _win = new MainWindow(_store, _toggle, _rotation);
        _win.Show();
        _win.EnsureHandleReady();
        _hwnd = _win.OwnHandle;
        Win32.MakeClickThroughAlwaysOnTop(_win);
        var source = System.Windows.Interop.HwndSource.FromHwnd(_hwnd);
        source?.AddHook(HotkeyHook);
        var okToggle = Win32.RegisterHotKey(_hwnd, Win32.HOTKEY_ID, Win32.MOD_CTRL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, Win32.VK_S);
        var okSettings = Win32.RegisterHotKey(_hwnd, Win32.HOTKEY_SETTINGS_ID, Win32.MOD_CTRL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, Win32.VK_O);
        AppLog.Trace($"app: hwnd=0x{_hwnd.ToInt64():X} source={(source != null)} toggleHotkey={okToggle} settingsHotkey={okSettings}");

        SetupTray();
        ReportLoadErrors();
        WatchConfig();

        // RegisterHotKey fails when another program owns the combination. Nothing else in the
        // app would ever tell you; the tray menu still works, so say so rather than exit.
        if (!okToggle || !okSettings)
        {
            var taken = string.Join(" and ", new[] { okToggle ? null : "Ctrl+Alt+S", okSettings ? null : "Ctrl+Alt+O" }
                .Where(k => k != null));
            AppLog.Warn($"could not register {taken}: already in use by another program");
            _tray?.ShowBalloonTip(8000, "ShortcutScout: hotkey unavailable",
                $"Another program already uses {taken}. The tray icon's menu does the same things.",
                WinForms.ToolTipIcon.Warning);
        }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, __) =>
        {
            // Caught here, not left to the dispatcher handler: DispatcherTimer re-arms only
            // after Tick returns normally, so an exception that escaped was "survived" by an
            // overlay whose timer never fired again — frozen, with the tray still alive.
            try { _tick++; _win?.Refresh(_tick); }
            catch (Exception ex) { SurviveOrStop("a tick failed", ex); }
        };
        _timer.Start();
        _win.Refresh(0);
        _started = true;
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case Win32.HOTKEY_ID:
                    ToggleHidden("hotkey");
                    handled = true;
                    break;
                case Win32.HOTKEY_SETTINGS_ID:
                    ShowSettings();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The overlay is click-through and never activates, so this window is the only
    /// way to tune it with a mouse. It's a normal window: activatable and hit-testable.
    /// </summary>
    private void ShowSettings()
    {
        AppLog.Trace("app: settings requested");
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(_store, () => _win?.ApplySettings());
        _settings.Closed += (_, __) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    private void ToggleHidden(string via)
    {
        _toggle.Toggle();
        AppLog.Info(_toggle.Hidden ? $"hidden ({via})" : $"shown again ({via})");
    }

    /// <summary>
    /// A WinExe has no console: an unhandled exception used to end the process with nothing
    /// written anywhere. Every route an exception can escape by now lands in the event log.
    /// </summary>
    private void InstallCrashLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;

            // Nothing to carry on with until the overlay is up — and --mcp, --cli and --sample
            // never get there. Surviving a startup failure left a process with no window, no
            // tray and no way out (holding the single-instance mutex, in the overlay's case).
            if (!_started)
            {
                AppLog.Error("startup failed", args.Exception);
                Shutdown(1);
                return;
            }

            SurviveOrStop("unhandled exception on the UI thread", args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error($"fatal exception (terminating={args.IsTerminating})", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>Log an error the running overlay can outlive, unless they keep coming.</summary>
    private void SurviveOrStop(string what, Exception ex)
    {
        AppLog.Error(what, ex);

        // One bad tick is survivable; the same failure twice a second is not.
        if (_errorBudget.Absorb(_uptime.Elapsed)) return;

        AppLog.Error($"{_errorBudget.MaxErrors} errors within {_errorBudget.Window.TotalSeconds:0}s; shutting down");
        _tray?.ShowBalloonTip(10000, "ShortcutScout stopped",
            "It kept hitting errors. See " + AppLog.EventLogPath, WinForms.ToolTipIcon.Error);
        Shutdown(1);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Warn($"could not open '{path}': {ex.Message}"); }
    }

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "ShortcutScout (Ctrl+Alt+S)",
            Visible = true
        };
        var menu = new WinForms.ContextMenuStrip();
        var settingsItem = new WinForms.ToolStripMenuItem("Settings… (Ctrl+Alt+O)");
        settingsItem.Click += (_, __) => ShowSettings();
        var toggleItem = new WinForms.ToolStripMenuItem("Hide / Show (Ctrl+Alt+S)");
        toggleItem.Click += (_, __) => ToggleHidden("tray");
        var reloadItem = new WinForms.ToolStripMenuItem("Reload config");
        reloadItem.Click += (_, __) => ReloadConfig("tray");
        var configItem = new WinForms.ToolStripMenuItem("Open config folder");
        configItem.Click += (_, __) => OpenFolder(_store.ConfigDir);
        var logItem = new WinForms.ToolStripMenuItem("Open log folder");
        logItem.Click += (_, __) => OpenFolder(AppLog.Folder);
        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, __) => Shutdown();
        menu.Items.Add(settingsItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add(reloadItem);
        menu.Items.Add(configItem);
        menu.Items.Add(logItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, __) => ShowSettings();
    }

    /// <summary>
    /// A config file that failed to parse is skipped, not fatal — but skipping it silently
    /// looks exactly like "my edit did nothing", so say which file and why.
    /// </summary>
    /// <param name="asked">
    /// True when the user explicitly asked for a reload: answer every time. Otherwise only
    /// speak up when the set of problems CHANGES — with the folder watched, a file that stays
    /// broken would otherwise raise the same balloon on every save of any other file.
    /// </param>
    private void ReportLoadErrors(bool asked = false)
    {
        var errors = _store.LoadErrors;
        var summary = string.Join(Environment.NewLine, errors);
        var changed = summary != _lastReportedErrors;
        _lastReportedErrors = summary;
        if (!changed && !asked) return;

        foreach (var error in errors) AppLog.Warn("config: " + error);
        if (errors.Count == 0)
        {
            if (changed) AppLog.Info("config: all files load cleanly again");
            return;
        }

        var text = string.Join(Environment.NewLine, errors.Take(3));
        if (text.Length > 240) text = text[..240] + "…";
        _tray?.ShowBalloonTip(8000, "ShortcutScout: config problem", text, WinForms.ToolTipIcon.Warning);
    }

    private string _lastReportedErrors = "";

    private void ReloadConfig(string via)
    {
        _store.Reload();
        _win?.ApplySettings();
        AppLog.Info($"config reloaded ({via}): apps={_store.Config.Apps.Count} " +
                    $"shortcuts={_store.Config.Apps.Sum(a => a.Shortcuts.Count)} errors={_store.LoadErrors.Count}");
        ReportLoadErrors(asked: via == "tray");
    }

    /// <summary>
    /// Pick up config edits without a trip to the tray menu.
    ///
    /// `--mcp` and `--cli` are separate processes: they write the files, and until now the
    /// running overlay never noticed, so an agent's add_shortcut looked like it did nothing.
    /// One save produces several file events (and a write-then-rename produces more), so the
    /// reload waits for them to go quiet.
    /// </summary>
    private void WatchConfig()
    {
        try
        {
            Directory.CreateDirectory(_store.ConfigDir);

            _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _reloadDebounce.Tick += (_, __) =>
            {
                _reloadDebounce!.Stop();

                // The settings window is an editor with unsaved in-memory changes; reloading
                // underneath it would overwrite the value being dragged. Its own saves are
                // what we would be reacting to anyway.
                if (_settings != null) return;
                ReloadConfig("file change");
            };

            _configWatcher = new FileSystemWatcher(_store.ConfigDir, "*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            // Raised on a thread-pool thread: only restart the timer, and do it on the UI thread.
            void Touched(object? sender, EventArgs args) =>
                Dispatcher.BeginInvoke(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); });

            _configWatcher.Changed += Touched;
            _configWatcher.Created += Touched;
            _configWatcher.Deleted += Touched;
            _configWatcher.Renamed += Touched;
            _configWatcher.Error += (_, args) =>
                AppLog.Warn("config watcher error (use the tray's Reload config): " + args.GetException().Message);
            _configWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // A network share or an odd filesystem: manual reload still works.
            AppLog.Warn("config folder is not being watched: " + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _configWatcher?.Dispose();
        _reloadDebounce?.Stop();
        _timer?.Stop();
        if (_hwnd != IntPtr.Zero)
        {
            Win32.UnregisterHotKey(_hwnd, Win32.HOTKEY_ID);
            Win32.UnregisterHotKey(_hwnd, Win32.HOTKEY_SETTINGS_ID);
        }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_singleInstance != null)
        {
            AppLog.Info($"exiting (code {e.ApplicationExitCode}) after {_uptime.Elapsed:d\\.hh\\:mm\\:ss}");
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>Diagnostic: sample an arbitrary screen rect and print what the overlay sees.</summary>
    private void RunSample(string[] args)
    {
        var i = Array.IndexOf(args, "--sample");
        // A typo ("70O") is a usage error, not an exception.
        if (i + 4 >= args.Length
            || !int.TryParse(args[i + 1], out var x) || !int.TryParse(args[i + 2], out var y)
            || !int.TryParse(args[i + 3], out var w) || !int.TryParse(args[i + 4], out var h))
        {
            Console.WriteLine("usage: --sample <x> <y> <w> <h>");
            return;
        }

        var stats = BackdropSampler.SampleRect(x, y, w, h);
        var theme = BackdropAdaptation.ThemeFor(stats);

        Console.WriteLine($"rgb=({stats.R:0},{stats.G:0},{stats.B:0}) luma={stats.Luma255:0} spread={stats.Spread:0}");
        Console.WriteLine($"busy={stats.IsBusy} -> keys=rgb({theme.KeyR},{theme.KeyG},{theme.KeyB}) " +
                          $"halo=rgb({theme.HaloR},{theme.HaloG},{theme.HaloB}) blur={theme.HaloBlur:0.0} " +
                          $"keyContrast={theme.KeyContrastOnHalo:0.0}:1 actionContrast={theme.ActionContrastOnHalo:0.0}:1");
    }

    private void RunMcp()
    {
        var server = new McpServer(_store);

        // MCP stdio is UTF-8, one message per LF-terminated line. A WinExe's redirected
        // console streams default to the OEM code page and CRLF, so wrap the raw handles.
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n", AutoFlush = true };

        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = server.Handle(line); // null for notifications: nothing may be sent
            if (response != null) output.WriteLine(response);
        }
    }

    private void RunCli(string[] args)
    {
        var i = Array.IndexOf(args, "--cli");
        var cmd = i >= 0 && i + 1 < args.Length ? args[i + 1] : "list";

        // No tray here to show a balloon; without this a skipped file just looks like missing apps.
        foreach (var error in _store.LoadErrors) Console.Error.WriteLine("warning: " + error);

        try { RunCliCommand(cmd, args, i); }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Text.Json.JsonException
                                       or UnauthorizedAccessException)
        {
            // A refused app id or an unwritable file is an answer, not a crash dialog.
            Console.WriteLine("error: " + ex.Message);
        }
    }

    private void RunCliCommand(string cmd, string[] args, int i)
    {
        switch (cmd)
        {
            case "list":
                foreach (var a in _store.Config.Apps)
                    Console.WriteLine($"{a.Id}\t{a.Name}\t{a.Shortcuts.Count} shortcuts");
                break;
            case "show" when i + 2 < args.Length:
                var app = _store.Config.Apps.FirstOrDefault(a => a.Id == args[i + 2]);
                if (app == null) { Console.WriteLine("app not found"); break; }
                foreach (var s in app.Shortcuts)
                    Console.WriteLine($"{s.Keys}\t{s.Action}{(s.Discovery ? "  [discovery]" : "")}");
                break;
            case "add" when i + 4 < args.Length:
                Console.WriteLine(_store.AddShortcut(args[i + 2], new Shortcut(args[i + 3], args[i + 4])) ? "added" : "already exists");
                break;
            case "remove" when i + 3 < args.Length:
                Console.WriteLine(_store.RemoveShortcut(args[i + 2], args[i + 3]) ? "removed" : "not found");
                break;
            default:
                Console.WriteLine("usage: --cli list | show <app> | add <app> <keys> <action> | remove <app> <keys>");
                break;
        }
    }
}
