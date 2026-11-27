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


    private long _lastWindowKey;

    // Pointer stillness, tracked independently of GetLastInputInfo (see CurrentIdleSeconds).
    private double _lastPointerX = double.NaN, _lastPointerY = double.NaN;
    private TimeSpan _pointerMovedAt = TimeSpan.Zero;

    /// <summary>Counts actual relocations, so each dodge picks a different clear position.</summary>
    private int _placementCount;

    private const double PointerMoveThresholdPx = 6;

    /// <summary>Which slice of the shortcut list is currently on screen.</summary>
    private long _rotationSlot;

    /// <summary>Set when the current peek already advanced the rotation.</summary>
    private bool _peekCountedRotation;

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

        // Growing while docked against the right or bottom edge would push the text off
        // screen, and the normal dodge only re-docks when the cursor is close. Re-place
        // now so a settings change can never strand the panel out of bounds.
        RunAwayFromCursor();
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

        var idle = ForceIdle ? int.MaxValue : CurrentIdleSeconds();
        var peekRemaining = (_peekUntil - _clock.Elapsed).TotalSeconds;

        var target = IdlePolicy.OpacityFor(settings, _toggle.Hidden, idle, peekRemaining);
        if (_obstructed && !_toggle.Hidden) target = Math.Min(target, 0.14);

        // Rotation counts *appearances*. A peek advances the slot when it starts (which is
        // the only event ambient mode ever gets, since the panel never becomes invisible),
        // and an appearance not caused by a peek — an idle reveal, or an un-hide whose peek
        // was refused by the anti-strobe floor — advances it here. The flag keeps one
        // appearance from being counted twice.
        //
        // This runs *before* UpdateContent: building the rows first meant the opening frame
        // of an idle reveal showed the previous slice and then swapped under the fade.
        if (target > 0.001 && _currentOpacity <= 0.001)
        {
            if (!_peekCountedRotation) _rotationSlot++;
            _peekCountedRotation = false;
        }
        else if (target <= 0.001)
        {
            _peekCountedRotation = false;
        }

        UpdateContent(settings);
        ApplyOpacity(target, settings);

        if (_toggle.Hidden) return;

        // Dodge on every tick, visible or not. Waiting until the panel is hidden meant
        // that during a peek — the only time you can actually see it — it sat under the
        // cursor and didn't move at all.
        RunAwayFromCursor();

        if (target <= 0.001) BeginBackdropSample();
    }

    /// <summary>
    /// How long the user has been away.
    ///
    /// `GetLastInputInfo` alone is not reliable here: measured on this machine it never
    /// exceeded 2s across 40s of a still, untouched desktop, so the 30s reveal could never
    /// fire. Something resets the OS input timer without moving the cursor. Pointer stillness
    /// gives a second signal, and the larger of the two wins.
    /// </summary>
    private int CurrentIdleSeconds()
    {
        var (px, py) = Win32.CursorPos();
        var moved = double.IsNaN(_lastPointerX)
            || Math.Abs(px - _lastPointerX) > PointerMoveThresholdPx
            || Math.Abs(py - _lastPointerY) > PointerMoveThresholdPx;

        if (moved)
        {
            _lastPointerX = px;
            _lastPointerY = py;
            _pointerMovedAt = _clock.Elapsed;
        }

        var still = (_clock.Elapsed - _pointerMovedAt).TotalSeconds;
        return IdlePolicy.CombineIdle(Win32.IdleSeconds(), still);
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
        var appId = app?.Id ?? "";

        // While explicitly hidden (Ctrl+Alt+S) a switch stays *pending*: consuming it would
        // swallow the change, so un-hiding would show nothing until an unrelated trigger.
        var windowKey = hwnd.ToInt64();
        var window = RevealPolicy.ObserveWindow(_lastWindowKey, windowKey, _toggle.Hidden);
        var windowChanged = window.Changed;

        // Title-matched apps are detected per *tab*, not per window: switching from Devin
        // to ChatGPT inside the same Chrome window changes the app but not the handle, so
        // an app change has to stay a reveal trigger in its own right.
        var appChanged = RevealPolicy.ObserveAppChange(_lastAppId, appId, _toggle.Hidden);

        // Decide the reveal BEFORE building rows: StartPeek advances the rotation slot, so
        // the slice that appears is the new one instead of swapping a tick later.
        var triggered = windowChanged || appChanged;
        var started = false;
        if (app is { Shortcuts.Count: > 0 } && !_toggle.Hidden)
        {
            if (windowChanged) started = StartPeek(settings, "window change");
            else if (appChanged) started = StartPeek(settings, "app change");
            else if (IdlePolicy.RePeekDue(settings, _toggle.Hidden,
                         (_clock.Elapsed - _lastRevealAt).TotalSeconds))
                started = StartPeek(settings, "re-peek");
        }

        // A trigger that the anti-strobe floor rejected stays *pending* and is retried on the
        // next tick: consuming it here would drop a real context switch on the floor (short
        // peeks make the cooldown longer than the peek itself).
        if (!_toggle.Hidden && (started || !triggered))
        {
            _lastWindowKey = windowKey;
            _lastAppId = appId;
        }

        var rows = new List<ShortcutRow>();
        if (app != null)
        {
            var keyWidth = IdlePolicy.KeysColumnFor(settings);
            foreach (var s in _rotation.Select(app.Shortcuts, Math.Clamp(settings.MaxRows, 1, 40), _rotationSlot))
                rows.Add(new ShortcutRow(s.Keys, s.Action, s.Discovery, keyWidth));
        }

        var signature = appId + "|" + string.Join(";", rows.Select(r => r.Keys + "=" + r.Action));
        if (signature == _lastSignature) return;

        var firstRender = _lastSignature.Length == 0;
        _lastSignature = signature;

        List.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Re-fade the rows when the list changes under a visible panel.
        if (rows.Count > 0 && !firstRender) RevealList();
    }

    /// <summary>Start a peek. Returns false when the anti-strobe floor refused it.</summary>
    private bool StartPeek(OverlaySettings settings, string reason)
    {
        if ((_clock.Elapsed - _lastPeekStart).TotalSeconds < MinPeekGapSeconds) return false;

        _lastPeekStart = _clock.Elapsed;

        // Measure the re-peek interval from here. Leaving this at its initial value made
        // RePeekDue true forever after the first interval, so the panel re-appeared every
        // four seconds and re_peek_seconds meant nothing.
        _lastRevealAt = _clock.Elapsed;

        _peekUntil = _clock.Elapsed + TimeSpan.FromSeconds(Math.Max(1, settings.PeekSeconds));

        // Each appearance shows the next slice. Advancing on the peek (rather than on the
        // hidden transition) also rotates in ambient mode, where the panel never goes
        // invisible and the old code therefore froze the list.
        _rotationSlot++;
        _peekCountedRotation = true;

        if (Tracing) Log($"peek started for '{_lastAppId}' ({reason}) slot={_rotationSlot} for {settings.PeekSeconds}s");
        return true;
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
            new CoreRect(work.X, work.Y, work.Width, work.Height),
            variety: _placementCount);

        _obstructed = !placement.Escaped;

        if (placement.Escaped &&
            (Math.Abs(placement.X - Left) > 0.5 || Math.Abs(placement.Y - Top) > 0.5))
        {
            Left = placement.X;
            Top = placement.Y;
            _placementCount++; // next dodge steps to a different clear position
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
