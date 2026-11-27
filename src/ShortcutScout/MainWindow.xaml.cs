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
    public sealed record ShortcutRow(string Keys, string Action, bool IsDiscovery, double KeyWidth);

    private readonly ConfigStore _store;
    private readonly ToggleState _toggle;
    private readonly Rotation _rotation;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Rate limit: switching windows quickly shouldn't strobe the panel.</summary>
    private const double MinPeekGapSeconds = 4;

    private double _currentOpacity = -1;
    private string _lastSignature = "";
    private string _lastAppId = "";
    private bool _obstructed;

    private BackdropStats _backdrop = BackdropStats.Empty;
    private TextTheme _theme = BackdropAdaptation.Default;
    private long _sampleCountdown;
    private bool _sampling;
    private long _lastTickAt;

    /// <summary>When the current peek ends.</summary>
    private TimeSpan _peekUntil = TimeSpan.MinValue;

    // Long enough ago that the first peek is never rate-limited (MinValue would overflow
    // when the gap is subtracted).
    private TimeSpan _lastPeekStart = TimeSpan.FromDays(-1);
    private TimeSpan _lastRevealAt;

    /// <summary>How many times the panel has become visible; drives rotation.</summary>
    private int _revealCount;

    private long _lastWindowKey;

    /// <summary>Which slice of the shortcut list is currently on screen.</summary>
    private long _rotationSlot;
    private bool _rotatedWhileHidden;

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
        _lastRevealAt = TimeSpan.Zero;
        ApplyLayout();
        ApplyTheme();
    }

    private OverlaySettings Settings => _store.Config.Overlay;

    public IntPtr OwnHandle => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    public void EnsureHandleReady()
    {
        if (OwnHandle == IntPtr.Zero) Show();
    }

    /// <summary>Re-read size/row settings, e.g. after the settings window saves.</summary>
    public void ApplySettings()
    {
        ApplyLayout();
        _lastSignature = ""; // rebuild rows at the new size / count
        UpdateContent(Settings);
    }

    private void ApplyLayout()
    {
        var s = Settings;
        Width = Math.Clamp(s.Width, 240, 1600);
        List.FontSize = Math.Clamp(s.FontSize, 8, 48);
        EmptyHint.FontSize = Math.Max(8, List.FontSize - 1);
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

        var settings = Settings;
        UpdateContent(settings);

        var idle = ForceIdle ? int.MaxValue : Win32.IdleSeconds();
        var peekRemaining = (_peekUntil - _clock.Elapsed).TotalSeconds;

        var target = IdlePolicy.OpacityFor(settings, _toggle.Hidden, idle, peekRemaining);
        if (_obstructed && !_toggle.Hidden) target = Math.Min(target, 0.14);

        if (target > 0.001)
        {
            _rotatedWhileHidden = false;
        }
        else if (!_rotatedWhileHidden)
        {
            // Advance the rotation the moment the panel goes away, not while it is on
            // screen: the next appearance is then already built from the new slice, so
            // rows never swap out from under you mid-peek.
            _rotatedWhileHidden = true;
            _revealCount++;
            _rotationSlot++;
            _lastSignature = "";
            if (Tracing) Log($"rotation -> slot {_rotationSlot} (after reveal #{_revealCount})");
        }

        ApplyOpacity(target, settings);

        if (_toggle.Hidden) return;

        // Dodge on every tick, visible or not. Waiting until the panel is hidden meant
        // that during a peek — the only time you can actually see it — it sat under the
        // cursor and didn't move at all.
        RunAwayFromCursor();

        if (target <= 0.001) BeginBackdropSample();
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

    private void UpdateContent(OverlaySettings settings)
    {
        var hwnd = Win32.GetForegroundWindow();
        var fg = ForegroundReader.Read();
        var matched = Matcher.Match(_store.Config.Apps, fg);
        var app = matched ?? _store.Config.Apps.FirstOrDefault(a => a.Id == "global");

        var rows = new List<ShortcutRow>();
        if (app != null)
        {
            var keyWidth = IdlePolicy.KeysColumnFor(settings);
            foreach (var s in _rotation.Select(app.Shortcuts, Math.Clamp(settings.MaxRows, 1, 40), _rotationSlot))
                rows.Add(new ShortcutRow(s.Keys, s.Action, s.Discovery, keyWidth));
        }

        var signature = (app?.Id ?? "") + "|" + string.Join(";", rows.Select(r => r.Keys + "=" + r.Action));
        if (signature != _lastSignature)
        {
            var firstRender = _lastSignature.Length == 0;
            _lastSignature = signature;
            _lastAppId = app?.Id ?? "";

            List.ItemsSource = rows;
            EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Re-fade the rows when the list changes under a visible panel.
            if (rows.Count > 0 && !firstRender) RevealList();
        }

        if (rows.Count == 0 || _toggle.Hidden) return;

        // Two reasons to show the list, and neither needs the matched *app* to change.
        // Keying on the app meant every unconfigured window collapsed into "global", so
        // moving between them counted as no change and the panel never appeared.
        var windowKey = hwnd.ToInt64();
        var windowChanged = windowKey != _lastWindowKey;
        _lastWindowKey = windowKey;

        if (windowChanged) StartPeek(settings, "window change");
        else if (IdlePolicy.RePeekDue(settings, _toggle.Hidden,
                     (_clock.Elapsed - _lastRevealAt).TotalSeconds))
            StartPeek(settings, "re-peek");
    }

    private void StartPeek(OverlaySettings settings, string reason)
    {
        if ((_clock.Elapsed - _lastPeekStart).TotalSeconds < MinPeekGapSeconds) return;

        _lastPeekStart = _clock.Elapsed;
        _peekUntil = _clock.Elapsed + TimeSpan.FromSeconds(Math.Max(1, settings.PeekSeconds));
        if (Tracing) Log($"peek started for '{_lastAppId}' ({reason}) for {settings.PeekSeconds}s");
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
