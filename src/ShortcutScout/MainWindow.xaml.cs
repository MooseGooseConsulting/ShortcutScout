using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ShortcutScout.Core;
using Shortcut = ShortcutScout.Core.Shortcut;
using CoreRect = ShortcutScout.Core.Rect;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace ShortcutScout;

public partial class MainWindow : Window
{
    /// <summary>Row rendered by the overlay template.</summary>
    public sealed record ShortcutRow(string Keys, string Action, bool IsDiscovery);

    private readonly ConfigStore _store;
    private readonly ToggleState _toggle;
    private readonly Rotation _rotation;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _currentOpacity = -1;
    private string _lastSignature = "";
    private string _lastAppId = "";
    private bool _obstructed;

    private BackdropStats _backdrop = BackdropStats.Empty;
    private TextTheme _theme = BackdropAdaptation.Default;
    private long _sampleCountdown;
    private bool _sampling;
    private long _lastTickAt;

    /// <summary>When the contextual peek (shown on app switch) ends.</summary>
    private TimeSpan _peekUntil = TimeSpan.MinValue;

    private static readonly bool Tracing =
        Environment.GetEnvironmentVariable("SHORTCUTSCOUT_TRACE") == "1";

    /// <summary>
    /// Debug hook: pretend the user has stepped away. Real input can't be faked
    /// reliably (any mouse movement resets GetLastInputInfo), so this makes the
    /// idle appearance reproducible for screenshots and tuning.
    /// </summary>
    private static readonly bool ForceIdle =
        Environment.GetEnvironmentVariable("SHORTCUTSCOUT_FORCE_IDLE") == "1";

    public MainWindow(ConfigStore store, ToggleState toggle, Rotation rotation)
    {
        InitializeComponent();
        _store = store;
        _toggle = toggle;
        _rotation = rotation;
        Left = OverlayGeometry.DefaultInset;
        Top = OverlayGeometry.DefaultInset;
        Opacity = 0; // start hidden; a peek or idle reveal brings it in
        _currentOpacity = 0;
        ApplyTheme();
    }

    private OverlaySettings Settings => _store.Config.Overlay;

    public IntPtr OwnHandle => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    public void EnsureHandleReady()
    {
        if (OwnHandle == IntPtr.Zero) Show();
    }

    public void Refresh(long tick)
    {
        if (Tracing)
        {
            // Objective stutter detection: the timer should fire every 500ms.
            var now = Stopwatch.GetTimestamp();
            if (_lastTickAt != 0)
            {
                var gapMs = (now - _lastTickAt) * 1000.0 / Stopwatch.Frequency;
                if (gapMs > 700) Log($"STALL tick gap {gapMs:0}ms");
            }
            _lastTickAt = now;
        }

        UpdateContent();

        var settings = Settings;
        var idle = ForceIdle ? int.MaxValue : Win32.IdleSeconds();
        var peekRemaining = (_peekUntil - _clock.Elapsed).TotalSeconds;

        var target = IdlePolicy.OpacityFor(settings, _toggle.Hidden, idle, peekRemaining);

        if (_obstructed && !_toggle.Hidden) target = Math.Min(target, 0.14);

        ApplyOpacity(target, settings);

        if (_toggle.Hidden) return;

        // Reposition only while invisible. Moving a visible panel is a motion onset,
        // which is the single most attention-grabbing thing a peripheral widget can do;
        // by dodging while unseen it simply never appears under the pointer.
        if (target <= 0.001)
        {
            RunAwayFromCursor();
            BeginBackdropSample();
        }
    }

    /// <summary>
    /// Sample what is behind the overlay so the halo can thicken over busy content.
    /// Runs OFF the UI thread: grabbing a few hundred thousand pixels on the
    /// dispatcher made the overlay visibly stutter.
    /// </summary>
    private async void BeginBackdropSample()
    {
        if (_sampling) return;
        if (_sampleCountdown-- > 0) return;
        _sampleCountdown = 4; // ~2s at the 500ms tick

        _sampling = true;
        try
        {
            var hwnd = OwnHandle;
            var started = Stopwatch.GetTimestamp();
            var stats = await Task.Run(() => BackdropSampler.Sample(hwnd));
            var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

            if (stats == BackdropStats.Empty) return;

            _backdrop = stats;
            ApplyTheme();

            if (Tracing)
            {
                var t = BackdropAdaptation.ThemeFor(stats);
                Log($"backdrop rgb=({stats.R:0},{stats.G:0},{stats.B:0}) luma={stats.Luma255:0} " +
                    $"spread={stats.Spread:0} busy={stats.IsBusy} " +
                    $"-> keys=({t.KeyR},{t.KeyG},{t.KeyB}) halo=({t.HaloR},{t.HaloG},{t.HaloB}) " +
                    $"blur={t.HaloBlur:0.0} sampleMs={elapsedMs:0.0}");
            }
        }
        catch
        {
            // Sampling is best-effort; never let it disturb the overlay.
        }
        finally
        {
            _sampling = false;
        }
    }

    /// <summary>
    /// Push the palette + halo for the current backdrop onto the visual tree.
    /// Brushes are resources so the DataTemplate picks the change up by itself.
    /// </summary>
    private void ApplyTheme()
    {
        var theme = BackdropAdaptation.ThemeFor(_backdrop);
        if (theme == _theme) return;
        _theme = theme;

        Resources["KeyBrush"] = new SolidColorBrush(
            MediaColor.FromRgb(theme.KeyR, theme.KeyG, theme.KeyB));
        Resources["ActionBrush"] = new SolidColorBrush(
            MediaColor.FromRgb(theme.ActionR, theme.ActionG, theme.ActionB));

        Halo.Color = MediaColor.FromRgb(theme.HaloR, theme.HaloG, theme.HaloB);
        Halo.Opacity = theme.HaloOpacity;
        Halo.BlurRadius = theme.HaloBlur;
    }

    private void UpdateContent()
    {
        var fg = ForegroundReader.Read();
        var matched = Matcher.Match(_store.Config.Apps, fg);
        var app = matched ?? _store.Config.Apps.FirstOrDefault(a => a.Id == "global");

        var rows = new List<ShortcutRow>();
        if (app != null)
        {
            var slot = Rotation.SlotFor(_clock.Elapsed);
            foreach (var s in _rotation.Select(app.Shortcuts, 8, slot))
                rows.Add(new ShortcutRow(s.Keys, s.Action, s.Discovery));
        }

        var signature = (app?.Id ?? "") + "|" + string.Join(";", rows.Select(r => r.Keys + "=" + r.Action));
        if (signature == _lastSignature) return;

        var appChanged = (app?.Id ?? "") != _lastAppId;
        _lastSignature = signature;
        _lastAppId = app?.Id ?? "";

        List.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (rows.Count > 0 && !appChanged) RevealList();

        // Contextual peek: switching to a configured app is the moment its shortcuts
        // are worth showing. This is the discovery mechanism — it does not need a
        // permanently visible panel to work.
        if (appChanged && rows.Count > 0 && !_toggle.Hidden)
        {
            _peekUntil = _clock.Elapsed + TimeSpan.FromSeconds(Math.Max(1, Settings.PeekSeconds));
            if (Tracing) Log($"peek started for '{app?.Id}' for {Settings.PeekSeconds}s");
        }
    }

    private void RevealList()
    {
        List.BeginAnimation(OpacityProperty, null);
        List.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void ApplyOpacity(double target, OverlaySettings settings)
    {
        Visibility = target <= 0 ? Visibility.Hidden : Visibility.Visible;

        if (Math.Abs(target - _currentOpacity) < 0.005) return;

        var from = Math.Max(_currentOpacity, 0);
        _currentOpacity = target;

        var duration = TimeSpan.FromMilliseconds(
            IdlePolicy.FadeDurationMs(settings, from, target));

        var anim = new DoubleAnimation(target, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>
    /// Keep the text out from under the pointer. WPF lays out in device-independent
    /// units while GetCursorPos reports physical pixels, so the cursor is converted
    /// before comparing them.
    /// </summary>
    private void RunAwayFromCursor()
    {
        var (px, py) = Win32.CursorPos();
        var source = PresentationSource.FromVisual(this);
        var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var cursor = toDip.Transform(new WpfPoint(px, py));

        var work = SystemParameters.WorkArea;
        var height = ActualHeight > 0 ? ActualHeight : 240;

        var placement = OverlayGeometry.Place(
            new CoreRect(Left, Top, Width, height),
            cursor.X, cursor.Y,
            new CoreRect(work.X, work.Y, work.Width, work.Height));

        _obstructed = !placement.Escaped;

        if (placement.Escaped)
        {
            if (Math.Abs(placement.X - Left) > 0.5) Left = placement.X;
            if (Math.Abs(placement.Y - Top) > 0.5) Top = placement.Y;
        }

        if (Tracing)
        {
            Log($"cursor(dip)=({cursor.X:0},{cursor.Y:0}) physical=({px},{py}) " +
                $"overlay=({Left:0},{Top:0},{Width:0}x{height:0}) " +
                $"-> ({placement.X:0},{placement.Y:0}) escaped={placement.Escaped} obstructed={_obstructed} " +
                $"idle={Win32.IdleSeconds()}s opacity={Opacity:0.00} target={_currentOpacity:0.00}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "shortcutscout-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* tracing must never break the overlay */ }
    }
}

internal static class ForegroundReader
{
    public static ForegroundInfo Read()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return new("", "", "");

        var sb = new StringBuilder(512);
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        Win32.GetWindowThreadProcessId(hwnd, out var pid);

        if (pid == 0) return new("", "", sb.ToString());
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return new(p.ProcessName, p.ProcessName + ".exe", sb.ToString());
        }
        catch
        {
            return new("", "", sb.ToString());
        }
    }
}
