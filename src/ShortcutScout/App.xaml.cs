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
    private readonly ConfigStore _store = new(FindConfig());
    private readonly ToggleState _toggle = new();
    private readonly Rotation _rotation = new();
    private DispatcherTimer? _timer;
    private WinForms.NotifyIcon? _tray;
    private IntPtr _hwnd;
    private long _tick;

    private static string FindConfig()
    {
        foreach (var p in new[] { "config", Path.Combine(AppContext.BaseDirectory, "config"),
                                  Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "config"),
                                  @"C:\_projects\ShortcutScout\config" })
            if (Directory.Exists(p)) return Path.GetFullPath(p);
        return "config";
    }

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
        System.Windows.Interop.HwndSource.FromHwnd(_hwnd)?.AddHook(HotkeyHook);
        Win32.RegisterHotKey(_hwnd, Win32.HOTKEY_ID, Win32.MOD_CTRL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, Win32.VK_S);

        SetupTray();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, __) => { _tick++; _win?.Refresh(_tick); };
        _timer.Start();
        _win.Refresh(0);
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == Win32.HOTKEY_ID)
        {
            _toggle.Toggle();
            handled = true;
        }
        return IntPtr.Zero;
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
        var toggleItem = new WinForms.ToolStripMenuItem("Hide / Show (Ctrl+Alt+S)");
        toggleItem.Click += (_, __) => _toggle.Toggle();
        var reloadItem = new WinForms.ToolStripMenuItem("Reload config");
        reloadItem.Click += (_, __) => _store.Reload();
        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, __) => Shutdown();
        menu.Items.Add(toggleItem);
        menu.Items.Add(reloadItem);
        menu.Items.Add(exitItem);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, __) => _toggle.Toggle();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        if (_hwnd != IntPtr.Zero) Win32.UnregisterHotKey(_hwnd, Win32.HOTKEY_ID);
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
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { Console.Out.WriteLine(server.Handle(line)); Console.Out.Flush(); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
        }
    }

    private void RunCli(string[] args)
    {
        var i = Array.IndexOf(args, "--cli");
        var cmd = i >= 0 && i + 1 < args.Length ? args[i + 1] : "list";
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
