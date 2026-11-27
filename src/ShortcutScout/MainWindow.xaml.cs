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

    /// <summary>
    /// When the current peek ends. <see cref="TimeSpan.Zero"/> means nothing is scheduled:
    /// that instant is already in the past, so `Refresh` reports no remaining peek time.
    /// Do not restore <see cref="TimeSpan.MinValue"/> — subtracting the elapsed clock from it
    /// overflows on the first tick and kills the app at startup.
    /// </summary>
    private TimeSpan _peekUntil = TimeSpan.Zero;

    // Long enough ago that the first peek is never rate-limited (MinValue would overflow
    // when the gap is subtracted).
    private TimeSpan _lastPeekStart = TimeSpan.FromDays(-1);
    private TimeSpan _lastRevealAt;


    private long _lastWindowKey;
    private AppConfig? _lastApp;

    /// <summary>Identifies the newest fade, so a superseded fade-out can't hide the window.</summary>
    private int _fadeGeneration;
    private bool _fadeOutRunning;

    // Pointer stillness, tracked independently of GetLastInputInfo (see CurrentIdleSeconds).
    private double _lastPointerX = double.NaN, _lastPointerY = double.NaN;
    private TimeSpan _pointerMovedAt = TimeSpan.Zero;

    /// <summary>Counts actual relocations, so each dodge picks a different clear position.</summary>
    private int _placementCount;

    /// <summary>When the visible rows last stepped to the next slice.</summary>
    private TimeSpan _lastSlideAt = TimeSpan.Zero;

    private const double PointerMoveThresholdPx = 6;

    /// <summary>Which slice of the shortcut list is currently on screen.</summary>
    private long _rotationSlot;

    /// <summary>Set when the current peek already advanced the rotation.</summary>
    private bool _peekCountedRotation;

    private static bool Tracing => AppLog.Tracing;

    /// <summary>Why the reveal that began on this tick began, when a peek started it.</summary>
    private string? _peekReasonThisTick;

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
        RenderRows(ObserveTriggers(Settings), Settings);

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

        // Triggers first: StartPeek inside here sets `_peekUntil` and advances the rotation
        // slot. Computing opacity before it meant a peek that began on this tick was applied
        // one tick late, and a re-peek landing on the tick the old one expired hid the panel
        // for 500ms — a visible flash.
        var app = ObserveTriggers(settings);

        var idle = ForceIdle ? int.MaxValue : CurrentIdleSeconds();
        var peekRemaining = _peekUntil <= _clock.Elapsed ? 0 : (_peekUntil - _clock.Elapsed).TotalSeconds;

        // Opacity and rotation are decided together, in Core, so the ordering rules are
        // covered by tests: `peekRemaining` above is already post-trigger, and the rotation
        // this returns is used by the rows below.
        var tickState = OverlayTicker.Decide(
            settings, _toggle.Hidden, idle, peekRemaining,
            _currentOpacity, _rotationSlot, _peekCountedRotation, _obstructed,
            secondsSinceSlide: (_clock.Elapsed - _lastSlideAt).TotalSeconds);

        _rotationSlot = tickState.RotationSlot;
        _peekCountedRotation = tickState.PeekCountedRotation;

        // One line per reveal, with its reason, in the always-on log. This is the line that
        // answers "why did it (not) appear?" — it must not depend on tracing having been on.
        if (_peekReasonThisTick != null)
        {
            AppLog.Info($"reveal: {_peekReasonThisTick} app='{app?.Id}' slot={_rotationSlot} " +
                        $"for {settings.PeekSeconds:0.#}s{(tickState.FreshAppearance ? "" : " (already visible)")}");
        }
        else if (tickState.FreshAppearance)
        {
            var why = IdlePolicy.IsIdle(idle, settings.IdleThresholdSeconds) ? $"idle {(ForceIdle ? "forced" : idle + "s")}"
                : peekRemaining > 0 ? "un-hidden during a peek"
                : "ambient";
            AppLog.Info($"reveal: {why} app='{app?.Id}' slot={_rotationSlot}");
        }

        // Cleared once logged, not at the top of the tick: ApplySettings can start a peek
        // between two ticks, and its reason has to survive until this line reports it.
        _peekReasonThisTick = null;

        // The slide clock restarts on a fresh appearance (so rows never change the instant the
        // panel arrives) and on every step; `Decide` owns both decisions so they stay testable.
        if (tickState.FreshAppearance || tickState.Slide)
        {
            _lastSlideAt = _clock.Elapsed;
            if (tickState.Slide && Tracing) Log($"rotate -> slot {_rotationSlot} (while visible)");
        }

        // Rows after the rotation decision, so the first frame of an appearance already shows
        // the slice that decision selected.
        RenderRows(app, settings);
        ApplyOpacity(tickState.Target, settings);

        if (_toggle.Hidden) return;

        // Dodge on every tick, visible or not. Waiting until the panel is hidden meant
        // that during a peek — the only time you can actually see it — it sat under the
        // cursor and didn't move at all.
        RunAwayFromCursor();

        if (tickState.Target <= 0.001) BeginBackdropSample();
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
        catch (Exception ex)
        {
            // Sampling is best-effort; never let it disturb the overlay.
            AppLog.Trace("backdrop sample failed: " + ex.Message);
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

    /// <summary>
    /// Watch the foreground window/app, and start a peek if this tick calls for one.
    /// Returns the matched app so the rows can be built from the same observation.
    /// </summary>
    private AppConfig? ObserveTriggers(OverlaySettings settings)
    {
        var hwnd = Win32.GetForegroundWindow();

        // No foreground window: focus is mid-switch, or the session is locked. Keep showing
        // what we had rather than "switching" to the fallback app for one tick.
        if (hwnd == IntPtr.Zero && _lastApp != null && _store.Config.Apps.Contains(_lastApp))
            return _lastApp;

        // Same handle for the title and the trigger: asking Windows twice could describe one
        // window and key the reveal on another.
        var fg = ForegroundReader.Read(hwnd);
        var matched = Matcher.Match(_store.Config.Apps, fg);
        var app = matched ?? _store.Config.Apps.FirstOrDefault(a => a.Id == "global");
        var appId = app?.Id ?? "";
        _lastApp = app;

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
            if (windowChanged) started = StartPeek(settings, appId, "window change");
            else if (appChanged) started = StartPeek(settings, appId, "app change");
            else if (IdlePolicy.RePeekDue(settings, _toggle.Hidden,
                         (_clock.Elapsed - _lastRevealAt).TotalSeconds))
                started = StartPeek(settings, appId, "re-peek");
        }

        // A trigger that the anti-strobe floor rejected stays *pending* and is retried on the
        // next tick: consuming it here would drop a real context switch on the floor (short
        // peeks make the cooldown longer than the peek itself).
        if (!_toggle.Hidden && (started || !triggered))
        {
            _lastWindowKey = window.WindowKey;
            _lastAppId = appId;
        }

        return app;
    }

    private void RenderRows(AppConfig? app, OverlaySettings settings)
    {
        var appId = app?.Id ?? "";
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
    private bool StartPeek(OverlaySettings settings, string appId, string reason)
    {
        if ((_clock.Elapsed - _lastPeekStart).TotalSeconds < MinPeekGapSeconds) return false;

        _lastPeekStart = _clock.Elapsed;

        // Measure the re-peek interval from here. Leaving this at its initial value made
        // RePeekDue true forever after the first interval, so the panel re-appeared every
        // four seconds and re_peek_seconds meant nothing.
        _lastRevealAt = _clock.Elapsed;
        _lastSlideAt = _clock.Elapsed; // new appearance: first slide is a full interval away

        _peekUntil = _clock.Elapsed + TimeSpan.FromSeconds(Math.Max(1, settings.PeekSeconds));

        // Each appearance shows the next slice. Advancing on the peek (rather than on the
        // hidden transition) also rotates in ambient mode, where the panel never goes
        // invisible and the old code therefore froze the list.
        _rotationSlot++;
        _peekCountedRotation = true;

        // `appId` is the app being revealed; `_lastAppId` is still the previous one here.
        if (Tracing) Log($"peek started for '{appId}' ({reason}) slot={_rotationSlot} for {settings.PeekSeconds}s");
        _peekReasonThisTick = reason;
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
        if (target > 0) Visibility = Visibility.Visible;

        if (Math.Abs(target - _currentOpacity) < 0.005)
        {
            // Already at rest. Hidden rather than merely transparent, unless a fade-out is
            // still on its way there.
            if (target <= 0 && !_fadeOutRunning) Visibility = Visibility.Hidden;
            return;
        }

        var from = Math.Max(_currentOpacity, 0);
        _currentOpacity = target;

        var duration = TimeSpan.FromMilliseconds(
            Math.Max(0, IdlePolicy.FadeDurationMs(settings, from, target)));

        var anim = new DoubleAnimation(target, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        // Hide the window when the fade-out FINISHES. Hiding it up front, as this did, removed
        // the window before the first frame of the animation: `fade_out_ms` did nothing and
        // the panel always vanished with a hard cut.
        var generation = ++_fadeGeneration;
        _fadeOutRunning = target <= 0;
        if (_fadeOutRunning)
        {
            anim.Completed += (_, __) =>
            {
                if (generation != _fadeGeneration) return; // a newer fade took over
                _fadeOutRunning = false;
                if (_currentOpacity <= 0) Visibility = Visibility.Hidden;
            };
        }

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

    /// <summary>Per-tick trace line; capped and a no-op unless SHORTCUTSCOUT_TRACE=1.</summary>
    private static void Log(string message) => AppLog.Trace(message);
}

internal static class ForegroundReader
{
    public static ForegroundInfo Read(IntPtr hwnd)
    {
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
