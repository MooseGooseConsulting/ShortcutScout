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
        // drag would silently rewrite it. Clamp the model itself: what you see is what saves.
        S.FontSize = Fit(FontSlider, S.FontSize);
        S.Width = Fit(WidthSlider, S.Width);
        S.MaxRows = (int)Fit(RowsSlider, S.MaxRows);
        S.PeekSeconds = Fit(PeekSlider, S.PeekSeconds);
        S.RePeekSeconds = (int)Fit(RePeekSlider, S.RePeekSeconds);
        S.IdleThresholdSeconds = (int)Fit(IdleSlider, S.IdleThresholdSeconds);
        S.ActiveOpacity = Fit(OpacitySlider, S.ActiveOpacity);

        RefreshValues();
    }

    private static double Fit(Slider slider, double value)
    {
        var clamped = Math.Clamp(value, slider.Minimum, slider.Maximum);
        slider.Value = clamped;
        return clamped;
    }

    private void RefreshValues()
    {
        FontValue.Text = $"{S.FontSize:0}";
        WidthValue.Text = $"{S.Width:0}";
        RowsValue.Text = $"{S.MaxRows:0}";
        PeekValue.Text = $"{S.PeekSeconds:0}s";
        RePeekValue.Text = S.RePeekSeconds == 0 ? "off" : $"{S.RePeekSeconds:0}s";
        IdleValue.Text = $"{S.IdleThresholdSeconds:0}s";
        OpacityValue.Text = $"{S.ActiveOpacity:0.00}";
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
        Save(); // never lose the last drag
        base.OnClosed(e);
    }
}
