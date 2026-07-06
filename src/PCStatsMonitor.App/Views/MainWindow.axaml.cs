using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PCStatsMonitor.App.Bootstrapping;
using PCStatsMonitor.App.ViewModels;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.App.Views;

public partial class MainWindow : Window
{
    // Parameterless ctor for Avalonia XAML designer only
    public MainWindow() : this(new MainViewModel()) { }

    private readonly MainViewModel _vm;
    private readonly SensorPump? _pump;
    private readonly AppSettings _settings;

    public MainWindow(MainViewModel vm, SensorPump? pump = null, AppSettings? settings = null)
    {
        _vm       = vm;
        _pump     = pump;
        _settings = settings ?? new AppSettings();
        DataContext = vm;
        InitializeComponent();
        WireSettingsEvents();
        ApplyTheme();

        // Wire snapshot updates from pump → UI thread
        if (_pump != null)
            _pump.SnapshotPublished += OnSnapshot;

        // Pause pump when minimized OR hidden to tray, resume when shown
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty && e.Property != IsVisibleProperty)
                return;
            bool nowVisible = IsVisible && WindowState != WindowState.Minimized;
            if (nowVisible == _uiVisible)
                return;
            _uiVisible = nowVisible;
            if (_pump != null)
                // Overlay counts as a watcher: pump must keep polling for it
                // even while this window is hidden/minimized
                _pump.WindowVisible = nowVisible || _settings.ShowOverlay;
            // Pump is paused while hidden — page out everything; OS pages back on show
            if (!nowVisible)
                Bootstrapping.WorkingSetTrimmer.Trim();
        };
    }

    // Written on UI thread, read on pump thread in OnSnapshot
    private volatile bool _uiVisible = true;

    private void OnSnapshot(SensorSnapshot snap)
    {
        // No UI work while hidden/minimized — gauges would animate unseen
        if (!_uiVisible) return;
        try
        {
            // Marshal to UI thread; use Background priority so it doesn't pre-empt user input
            Dispatcher.UIThread.Post(() => _vm.Apply(snap), DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            // App is exiting: the dispatcher shut down between the pump publishing this
            // snapshot and our post. Dropping the frame is correct — never crash for it.
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // App/OS-initiated shutdown (tray Exit, our own Exit choice, logoff) must
        // never be intercepted — desktop.Shutdown() closes windows through here.
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            base.OnClosing(e);
            return;
        }

        switch (_settings.CloseBehavior)
        {
            case CloseBehavior.MinimizeToTray:
                e.Cancel = true;
                Hide();
                break;

            case CloseBehavior.Exit:
                e.Cancel = true;
                ExitApp();
                break;

            default: // Ask — no preference stored yet
                e.Cancel = true;
                ClosePromptOverlay.IsVisible = true;
                break;
        }
    }

    private static void ExitApp()
    {
        // Shutdown() (unlike TryShutdown) overrides window-closing cancellation and
        // runs the desktop.Exit cleanup chain (pump/tray/guard disposal).
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void ApplyPromptChoice(CloseBehavior choice)
    {
        ClosePromptOverlay.IsVisible = false;
        if (RememberChoiceCheck.IsChecked == true)
        {
            _settings.CloseBehavior = choice;
            _settings.Save();
        }
        if (choice == CloseBehavior.MinimizeToTray)
            Hide();
        else
            ExitApp();
    }

    private void OnPromptTrayClick(object? sender, RoutedEventArgs e) =>
        ApplyPromptChoice(CloseBehavior.MinimizeToTray);

    private void OnPromptExitClick(object? sender, RoutedEventArgs e) =>
        ApplyPromptChoice(CloseBehavior.Exit);

    // True while OnSettingsClick populates the controls — their change events
    // must not commit half-populated state
    private bool _suppressSettingEvents;

    /// <summary>Instant-apply: every control in the settings panel commits on
    /// change so the user sees the overlay update live; Done only closes.</summary>
    private void WireSettingsEvents()
    {
        SettingTrayRadio.IsCheckedChanged  += (_, _) => CommitSettings();
        SettingExitRadio.IsCheckedChanged  += (_, _) => CommitSettings();
        SettingAskRadio.IsCheckedChanged   += (_, _) => CommitSettings();
        SettingOverlayCheck.IsCheckedChanged += (_, _) => CommitSettings();
        SettingOverlayPos.SelectionChanged   += (_, _) => CommitSettings();
        SettingOverlaySize.SelectionChanged  += (_, _) => CommitSettings();
        SettingShowFps.IsCheckedChanged  += (_, _) => CommitSettings();
        SettingShowCpu.IsCheckedChanged  += (_, _) => CommitSettings();
        SettingShowGpu.IsCheckedChanged  += (_, _) => CommitSettings();
        SettingShowRam.IsCheckedChanged  += (_, _) => CommitSettings();
        SettingShowDisk.IsCheckedChanged += (_, _) => CommitSettings();
        SettingShowTemps.IsCheckedChanged += (_, _) => CommitSettings();
        SettingOverlayIcons.IsCheckedChanged += (_, _) => CommitSettings();
        SettingPotato.IsCheckedChanged   += (_, _) => CommitSettings();
        SettingMonotone.IsCheckedChanged += (_, _) => CommitSettings();
        SettingOverlayBg.SelectionChanged += (_, _) => CommitSettings();
        SettingCorner.SelectionChanged    += (_, _) => CommitSettings();
        SettingOverlayFont.SelectionChanged += (_, _) => CommitSettings();
        SettingFontSize.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                CommitSettings();
        };
        SettingOpacity.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                CommitSettings();
        };
        SettingOffsetY.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                CommitSettings();
        };
        SettingOffsetX.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                CommitSettings();
        };
    }

    private void CommitSettings()
    {
        if (_suppressSettingEvents)
            return;

        // An overlay with zero metrics is an empty pill — clamp back to CPU
        if (SettingShowFps.IsChecked != true
            && SettingShowCpu.IsChecked != true && SettingShowGpu.IsChecked != true
            && SettingShowRam.IsChecked != true && SettingShowDisk.IsChecked != true)
        {
            _suppressSettingEvents = true;
            SettingShowCpu.IsChecked = true;
            _suppressSettingEvents = false;
        }

        _settings.CloseBehavior = SettingTrayRadio.IsChecked == true ? CloseBehavior.MinimizeToTray
                                : SettingExitRadio.IsChecked == true ? CloseBehavior.Exit
                                : CloseBehavior.Ask;
        _settings.ShowOverlay = SettingOverlayCheck.IsChecked == true;
        if (SettingOverlayPos.SelectedIndex >= 0)
            _settings.OverlayPosition = (OverlayPosition)SettingOverlayPos.SelectedIndex;
        if (SettingOverlaySize.SelectedIndex >= 0)
            _settings.OverlaySize = (OverlaySize)SettingOverlaySize.SelectedIndex;
        _settings.OverlayShowFps  = SettingShowFps.IsChecked == true;
        _settings.OverlayShowCpu  = SettingShowCpu.IsChecked == true;
        _settings.OverlayShowGpu  = SettingShowGpu.IsChecked == true;
        _settings.OverlayShowRam  = SettingShowRam.IsChecked == true;
        _settings.OverlayShowDisk = SettingShowDisk.IsChecked == true;
        _settings.OverlayShowTemps = SettingShowTemps.IsChecked == true;
        _settings.OverlayFontSize = SettingFontSize.Value;
        _settings.OverlayOpacity = SettingOpacity.Value / 100.0;
        if (SettingOverlayBg.SelectedIndex >= 0)
            _settings.OverlayBackground = (OverlayBackground)SettingOverlayBg.SelectedIndex;
        if (SettingCorner.SelectedIndex >= 0)
            _settings.OverlayCorner = (OverlayCorner)SettingCorner.SelectedIndex;
        _settings.OverlayOffsetY = (int)SettingOffsetY.Value;
        _settings.OverlayOffsetX = (int)SettingOffsetX.Value;
        _settings.OverlayIcons = SettingOverlayIcons.IsChecked == true;
        _settings.PotatoMode = SettingPotato.IsChecked == true;
        _settings.Monotone = SettingMonotone.IsChecked == true;
        if (SettingOverlayFont.SelectedIndex >= 0)
            _settings.OverlayFontIndex = SettingOverlayFont.SelectedIndex;
        SettingFontSizeValue.Text = SettingFontSize.Value.ToString("0.#");
        SettingOpacityValue.Text = $"{SettingOpacity.Value:0}%";
        SettingOffsetYValue.Text = $"{SettingOffsetY.Value:0}";
        SettingOffsetXValue.Text = $"{SettingOffsetX.Value:0}";
        ApplyTheme();
        _settings.Save(); // Changed event → App syncs overlay/tray live
    }

    /// <summary>Applies potato/monotone to the dashboard: gauge animation mode,
    /// accent colors (panel Tags feed the accent dot + gauge arc via binding).</summary>
    private void ApplyTheme()
    {
        Controls.CircularGauge.SetPotatoMode(_settings.PotatoMode);
        bool mono = _settings.Monotone;
        CpuPanel.Tag = mono ? "C9CBCF" : "3987E5";
        GpuPanel.Tag = mono ? "C9CBCF" : "D55181";
        MemPanel.Tag = mono ? "C9CBCF" : "199E70";
        StoPanel.Tag = mono ? "C9CBCF" : "C98500";
        LiveDot.Fill = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(mono ? "#8A8D93" : "#2ECC71"));
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        _suppressSettingEvents = true;
        SettingTrayRadio.IsChecked = _settings.CloseBehavior == CloseBehavior.MinimizeToTray;
        SettingExitRadio.IsChecked = _settings.CloseBehavior == CloseBehavior.Exit;
        SettingAskRadio.IsChecked  = _settings.CloseBehavior == CloseBehavior.Ask;
        SettingOverlayCheck.IsChecked = _settings.ShowOverlay;
        // ComboBox item orders mirror the OverlayPosition / OverlaySize enum orders
        SettingOverlayPos.SelectedIndex = (int)_settings.OverlayPosition;
        SettingOverlaySize.SelectedIndex = (int)_settings.OverlaySize;
        SettingShowFps.IsChecked  = _settings.OverlayShowFps;
        SettingShowCpu.IsChecked  = _settings.OverlayShowCpu;
        SettingShowGpu.IsChecked  = _settings.OverlayShowGpu;
        SettingShowRam.IsChecked  = _settings.OverlayShowRam;
        SettingShowDisk.IsChecked = _settings.OverlayShowDisk;
        SettingShowTemps.IsChecked = _settings.OverlayShowTemps;
        SettingFontSize.Value = _settings.OverlayFontSize;
        SettingOpacity.Value = _settings.OverlayOpacity * 100.0;
        SettingOverlayBg.SelectedIndex = (int)_settings.OverlayBackground;
        SettingCorner.SelectedIndex = (int)_settings.OverlayCorner;
        SettingOffsetY.Value = _settings.OverlayOffsetY;
        SettingOffsetX.Value = _settings.OverlayOffsetX;
        SettingOverlayIcons.IsChecked = _settings.OverlayIcons;
        SettingPotato.IsChecked = _settings.PotatoMode;
        SettingMonotone.IsChecked = _settings.Monotone;
        SettingOverlayFont.SelectedIndex = _settings.OverlayFontIndex;
        SettingFontSizeValue.Text = _settings.OverlayFontSize.ToString("0.#");
        SettingOpacityValue.Text = $"{_settings.OverlayOpacity * 100:0}%";
        SettingOffsetYValue.Text = _settings.OverlayOffsetY.ToString();
        SettingOffsetXValue.Text = _settings.OverlayOffsetX.ToString();
        _suppressSettingEvents = false;
        SettingsOverlay.IsVisible = true;
    }

    private void OnSettingsDoneClick(object? sender, RoutedEventArgs e) =>
        SettingsOverlay.IsVisible = false;

    public void ShowAndActivate()
    {
        // Snapshots were skipped while hidden — show current data immediately
        if (_pump != null)
            _vm.Apply(_pump.Current);
        Show();
        WindowState = WindowState.Normal;
        Activate();
        // Brief Topmost flip to force foreground focus on Windows
        Topmost = true;
        Topmost = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_pump != null)
            _pump.SnapshotPublished -= OnSnapshot;
        base.OnClosed(e);
    }
}
