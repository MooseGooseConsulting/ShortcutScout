using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ShortcutScout.Core;

namespace ShortcutScout;

/// <summary>
/// The only interactive surface in the app: the overlay itself is click-through and
/// never takes focus, so tuning happens here (tray menu or Ctrl+Alt+O).
/// Every change is applied live and debounced into config/shortcuts.json.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly Action _onChanged;
    private readonly DispatcherTimer _saveDebounce;
    private bool _ready;

    /// <summary>Only write the file when something was actually changed.</summary>
    private bool _dirty;

    public SettingsWindow(ConfigStore store, Action onChanged)
    {
        InitializeComponent();
        _store = store;
        _onChanged = onChanged;

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveDebounce.Tick += (_, __) => { _saveDebounce.Stop(); Save(); };

        Load();
        Wire();

        CloseButton.Click += (_, __) => Close();
        ConfigButton.Click += (_, __) => OpenConfig();
        _ready = true;
    }

    private OverlaySettings S => _store.Config.Overlay;

    private void Load()
    {
        ModeBox.SelectedIndex = IdlePolicy.ParseMode(S.Mode) == OverlayMode.Ambient ? 1 : 0;

        // Config can hold values outside a slider's range (font_size 40, say). Assigning
        // those clamps the thumb but leaves the label reading the raw number, so the next
        // drag would silently rewrite it. Clamp the slider position only; leave the model
        // untouched, so nothing is saved unless the user actually moves something.
        Show(FontSlider, S.FontSize);
        Show(WidthSlider, S.Width);
        Show(RowsSlider, S.MaxRows);
        Show(PeekSlider, S.PeekSeconds);
        Show(RePeekSlider, S.RePeekSeconds);
        Show(IdleSlider, S.IdleThresholdSeconds);
        Show(OpacitySlider, S.ActiveOpacity);

        RefreshValues();
    }

    /// <summary>
    /// Put a config value on a slider without touching the config.
    ///
    /// Clamping writes back only when the user actually drags: opening this window must not
    /// silently rewrite a hand-edited value that sits outside the slider's range.
    /// </summary>
    private static void Show(Slider slider, double value) =>
        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);

    /// <summary>
    /// Labels read the sliders, not the model: otherwise a value outside the slider's range
    /// shows a number the thumb can't reach, and the two disagree about what would be saved.
    /// </summary>
    private void RefreshValues()
    {
        FontValue.Text = $"{FontSlider.Value:0}";
        WidthValue.Text = $"{WidthSlider.Value:0}";
        RowsValue.Text = $"{RowsSlider.Value:0}";
        PeekValue.Text = $"{PeekSlider.Value:0}s";
        RePeekValue.Text = RePeekSlider.Value == 0 ? "off" : $"{RePeekSlider.Value:0}s";
        IdleValue.Text = $"{IdleSlider.Value:0}s";
        OpacityValue.Text = $"{OpacitySlider.Value:0.00}";
    }

    private void Wire()
    {
        // Setting the value in Load() must not trigger a write, hence _ready.
        FontSlider.ValueChanged += (_, e) => Changed(() => S.FontSize = e.NewValue);
        WidthSlider.ValueChanged += (_, e) => Changed(() => S.Width = e.NewValue);
        RowsSlider.ValueChanged += (_, e) => Changed(() => S.MaxRows = (int)e.NewValue);
        PeekSlider.ValueChanged += (_, e) => Changed(() => S.PeekSeconds = e.NewValue);
        RePeekSlider.ValueChanged += (_, e) => Changed(() => S.RePeekSeconds = (int)e.NewValue);
        IdleSlider.ValueChanged += (_, e) => Changed(() => S.IdleThresholdSeconds = (int)e.NewValue);
        OpacitySlider.ValueChanged += (_, e) => Changed(() => S.ActiveOpacity = e.NewValue);

        ModeBox.SelectionChanged += (_, __) =>
        {
            if (!_ready) return;
            S.Mode = IdlePolicy.ParseMode(ModeBox.SelectedIndex == 1 ? "ambient" : "peek") == OverlayMode.Ambient
                ? "ambient" : "peek";
            AfterChange();
        };
    }

    private void Changed(Action mutate)
    {
        if (!_ready) return;
        mutate();
        AfterChange();
    }

    private void AfterChange()
    {
        _dirty = true;
        RefreshValues();
        _onChanged();               // apply to the live overlay immediately
        _saveDebounce.Stop();
        _saveDebounce.Start();      // ...but only hit the disk once the dragging stops
    }

    private void Save()
    {
        try { _store.SaveOverlay(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "Could not save settings"); }
    }

    private void OpenConfig()
    {
        try { Process.Start(new ProcessStartInfo(_store.ConfigDir) { UseShellExecute = true }); }
        catch { /* nothing useful to do if the shell refuses */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        _saveDebounce.Stop();
        if (_dirty) Save(); // never lose the last drag, but never rewrite untouched config
        base.OnClosed(e);
    }
}
