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

    // Beside the exe first (an installed copy), walking up to the repo's config/ for a dev
    // build; the working directory is only a fallback, since a Start-menu or startup-folder
    // launch runs from wherever the shell happens to be.
    private static string FindConfig() =>
        ConfigLocator.Find(new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() });

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--mcp")) { RunMcp(); Shutdown(); return; }
        if (e.Args.Contains("--sample")) { RunSample(e.Args); Shutdown(); return; }
        if (e.Args.Contains("--cli")) { RunCli(e.Args); Shutdown(); return; }

        _win = new MainWindow(_store, _toggle, _rotation);
        _win.Show();
        _win.EnsureHandleReady();
        _hwnd = _win.OwnHandle;
        Win32.MakeClickThroughAlwaysOnTop(_win);
        var source = System.Windows.Interop.HwndSource.FromHwnd(_hwnd);
        source?.AddHook(HotkeyHook);
        var okToggle = Win32.RegisterHotKey(_hwnd, Win32.HOTKEY_ID, Win32.MOD_CTRL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, Win32.VK_S);
        var okSettings = Win32.RegisterHotKey(_hwnd, Win32.HOTKEY_SETTINGS_ID, Win32.MOD_CTRL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, Win32.VK_O);
        Log($"hwnd=0x{_hwnd.ToInt64():X} source={(source != null)} toggleHotkey={okToggle} settingsHotkey={okSettings}");

        SetupTray();
        ReportLoadErrors();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, __) => { _tick++; _win?.Refresh(_tick); };
        _timer.Start();
        _win.Refresh(0);
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            Log($"hotkey id={wParam.ToInt32()}");
            switch (wParam.ToInt32())
            {
                case Win32.HOTKEY_ID:
                    _toggle.Toggle();
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
        Log("settings requested");
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

    private static readonly bool Tracing =
        Environment.GetEnvironmentVariable("SHORTCUTSCOUT_TRACE") == "1";

    private static void Log(string message)
    {
        if (!Tracing) return;
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "shortcutscout-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff} app: {message}{Environment.NewLine}");
        }
        catch { /* tracing must never break the app */ }
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
        toggleItem.Click += (_, __) => _toggle.Toggle();
        var reloadItem = new WinForms.ToolStripMenuItem("Reload config");
        reloadItem.Click += (_, __) => { _store.Reload(); _win?.ApplySettings(); ReportLoadErrors(); };
        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, __) => Shutdown();
        menu.Items.Add(settingsItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add(reloadItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, __) => ShowSettings();
    }

    /// <summary>
    /// A config file that failed to parse is skipped, not fatal — but skipping it silently
    /// looks exactly like "my edit did nothing", so say which file and why.
    /// </summary>
    private void ReportLoadErrors()
    {
        var errors = _store.LoadErrors;
        foreach (var error in errors) Log("config: " + error);
        if (errors.Count == 0 || _tray == null) return;

        var text = string.Join(Environment.NewLine, errors.Take(3));
        if (text.Length > 240) text = text[..240] + "…";
        _tray.ShowBalloonTip(8000, "ShortcutScout: config problem", text, WinForms.ToolTipIcon.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        if (_hwnd != IntPtr.Zero)
        {
            Win32.UnregisterHotKey(_hwnd, Win32.HOTKEY_ID);
            Win32.UnregisterHotKey(_hwnd, Win32.HOTKEY_SETTINGS_ID);
        }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }

    /// <summary>Diagnostic: sample an arbitrary screen rect and print what the overlay sees.</summary>
    private void RunSample(string[] args)
    {
        var i = Array.IndexOf(args, "--sample");
        if (i + 4 >= args.Length)
        {
            Console.WriteLine("usage: --sample <x> <y> <w> <h>");
            return;
        }

        var x = int.Parse(args[i + 1]);
        var y = int.Parse(args[i + 2]);
        var w = int.Parse(args[i + 3]);
        var h = int.Parse(args[i + 4]);

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
