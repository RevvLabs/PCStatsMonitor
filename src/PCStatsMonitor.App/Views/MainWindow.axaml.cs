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
    private readonly UpdateService? _updates;

    public MainWindow(MainViewModel vm, SensorPump? pump = null, AppSettings? settings = null,
                      UpdateService? updates = null)
    {
        _vm       = vm;
        _pump     = pump;
        _settings = settings ?? new AppSettings();
        _updates  = updates;
        DataContext = vm;
        InitializeComponent();
        WireSettingsEvents();
        ApplyTheme();

        SettingsVersionText.Text = $"v{UpdateService.CurrentVersion.ToString(3)}";
        if (_updates != null)
            _ = AutoCheckForUpdatesAsync();

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
            // The settings page may be open underneath the prompt — its radios
            // still show the pre-choice preference
            if (SettingsOverlay.IsVisible)
                PopulateSettings();
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
        // Sidebar nav — switches the visible page; not a setting, so no commit
        NavGeneral.IsCheckedChanged    += (_, _) => UpdateSettingsPage();
        NavOverlay.IsCheckedChanged    += (_, _) => UpdateSettingsPage();
        NavAppearance.IsCheckedChanged += (_, _) => UpdateSettingsPage();

        SettingTrayRadio.IsCheckedChanged  += (_, _) => CommitSettings();
        SettingExitRadio.IsCheckedChanged  += (_, _) => CommitSettings();
        SettingAskRadio.IsCheckedChanged   += (_, _) => CommitSettings();
        SettingStartup.IsCheckedChanged    += (_, _) => CommitSettings();
        SettingOverlayCheck.IsCheckedChanged += (_, _) => CommitSettings();
        SettingOverlayPos.SelectionChanged   += (_, _) => CommitSettings();
        SettingOverlaySize.SelectionChanged  += (_, _) => CommitSettings();
        SettingOverlayMonitor.SelectionChanged += (_, _) => CommitSettings();
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

    /// <summary>Shows the settings page for the selected sidebar tab.</summary>
    private void UpdateSettingsPage()
    {
        PageGeneral.IsVisible    = NavGeneral.IsChecked == true;
        PageOverlay.IsVisible    = NavOverlay.IsChecked == true;
        PageAppearance.IsVisible = NavAppearance.IsChecked == true;
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
        _settings.StartWithWindows = SettingStartup.IsChecked == true;
        _settings.ShowOverlay = SettingOverlayCheck.IsChecked == true;
        if (SettingOverlayPos.SelectedIndex >= 0)
            _settings.OverlayPosition = (OverlayPosition)SettingOverlayPos.SelectedIndex;
        if (SettingOverlaySize.SelectedIndex >= 0)
            _settings.OverlaySize = (OverlaySize)SettingOverlaySize.SelectedIndex;
        if (SettingOverlayMonitor.SelectedIndex >= 0)
            _settings.OverlayMonitorIndex = SettingOverlayMonitor.SelectedIndex;
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
        PopulateSettings();
        SettingsOverlay.IsVisible = true;
    }

    /// <summary>Loads current settings into the page controls. Must run every
    /// time the page becomes visible — a page left open across hide-to-tray or
    /// a close-prompt choice holds stale radios, and CommitSettings would write
    /// that stale state back over the user's saved preference.</summary>
    private void PopulateSettings()
    {
        _suppressSettingEvents = true;
        SettingTrayRadio.IsChecked = _settings.CloseBehavior == CloseBehavior.MinimizeToTray;
        SettingExitRadio.IsChecked = _settings.CloseBehavior == CloseBehavior.Exit;
        SettingAskRadio.IsChecked  = _settings.CloseBehavior == CloseBehavior.Ask;
        SettingStartup.IsChecked = _settings.StartWithWindows;
        SettingOverlayCheck.IsChecked = _settings.ShowOverlay;
        // ComboBox item orders mirror the OverlayPosition / OverlaySize enum orders
        SettingOverlayPos.SelectedIndex = (int)_settings.OverlayPosition;
        SettingOverlaySize.SelectedIndex = (int)_settings.OverlaySize;
        PopulateMonitors();
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
        // Reset a stale "Up to date"/"Check failed" label from a previous visit
        if (_pendingUpdate is null && !_updateBusy)
            SettingsUpdateButton.Content = "Check for updates";
    }

    private void OnSettingsDoneClick(object? sender, RoutedEventArgs e) =>
        SettingsOverlay.IsVisible = false;

    /// <summary>Keeps the settings-page overlay toggle in sync when the Alt+M
    /// global hotkey flips ShowOverlay while the page is open.</summary>
    /// <summary>Fills the monitor picker from the live display list; the row is
    /// hidden entirely on single-monitor machines. Runs under _suppressSettingEvents
    /// (called from PopulateSettings), so rebuilding items won't commit.</summary>
    private void PopulateMonitors()
    {
        var all = Screens?.All;
        int count = all?.Count ?? 0;
        // Always show the row so the feature is discoverable; with one display it
        // just lists that single monitor. Extra monitors appear when plugged in.
        OverlayMonitorRow.IsVisible = count > 0;
        if (count == 0)
            return;

        var labels = new System.Collections.Generic.List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var b = all![i].Bounds;
            string tag = all[i].IsPrimary ? " (primary)" : string.Empty;
            labels.Add($"Display {i + 1}{tag} — {b.Width}×{b.Height}");
        }
        SettingOverlayMonitor.ItemsSource = labels;
        SettingOverlayMonitor.SelectedIndex = Math.Clamp(_settings.OverlayMonitorIndex, 0, count - 1);
    }

    public void RefreshOverlayToggle()
    {
        if (!SettingsOverlay.IsVisible)
            return;
        _suppressSettingEvents = true;
        SettingOverlayCheck.IsChecked = _settings.ShowOverlay;
        _suppressSettingEvents = false;
    }

    // ── Updates (Discord-style) ────────────────────────────────────────────
    // Zip release asset: downloaded + staged silently in the background, then
    // the banner offers a one-click "Restart now" (dismissing is fine — the
    // pending update applies automatically at the next launch). Releases
    // without a zip fall back to the silent installer.

    private UpdateInfo? _pendingUpdate;
    private bool _updateStaged;
    private bool _updateBusy;
    // Progress<T>.Report posts to the UI thread asynchronously, so the final
    // "100%" report can land AFTER the completion handler already set the button
    // to "Restart to update" — clobbering it back to a stuck "Downloading… 100%".
    // The download phase gates the progress text: cleared before the terminal
    // state, a late-arriving report becomes a no-op.
    private bool _downloadingProgress;

    private async Task AutoCheckForUpdatesAsync()
    {
        // Off the startup critical path; failures (offline, rate limit) are silent —
        // the settings-page button remains as the manual retry
        await Task.Delay(TimeSpan.FromSeconds(5));
        try
        {
            var info = await _updates!.CheckAsync();
            if (info is null)
                return;
            if (info.IsZipPatch)
            {
                // Silent background download; the user only ever sees "ready"
                await _updates.DownloadAndStageAsync(info, progress: null);
                Dispatcher.UIThread.Post(() => ShowUpdateReady(info));
            }
            else
            {
                Dispatcher.UIThread.Post(() => ShowUpdateAvailable(info));
            }
        }
        catch { }
    }

    /// <summary>Zip patch staged — one restart applies it.</summary>
    private void ShowUpdateReady(UpdateInfo info)
    {
        _pendingUpdate = info;
        _updateStaged = true;
        UpdateBannerText.Text = $"PC Stats Monitor {info.TagName} is ready — restart to apply";
        UpdateBannerButton.Content = "Restart now";
        UpdateBanner.IsVisible = true;
        SettingsUpdateButton.Content = "Restart to update";
    }

    /// <summary>Installer fallback — needs an explicit download+install click.</summary>
    private void ShowUpdateAvailable(UpdateInfo info)
    {
        _pendingUpdate = info;
        UpdateBannerText.Text = $"PC Stats Monitor {info.TagName} is available";
        UpdateBannerButton.Content = "Update now";
        UpdateBanner.IsVisible = true;
        SettingsUpdateButton.Content = $"Update to {info.TagName}";
    }

    private void OnUpdateNowClick(object? sender, RoutedEventArgs e) =>
        _ = ApplyUpdateAsync();

    private void OnUpdateDismissClick(object? sender, RoutedEventArgs e) =>
        UpdateBanner.IsVisible = false;

    /// <summary>Settings-page button: manual check while idle, apply once an
    /// update is known.</summary>
    private async void OnCheckUpdatesClick(object? sender, RoutedEventArgs e)
    {
        if (_updates is null || _updateBusy)
            return;

        if (_pendingUpdate != null)
        {
            await ApplyUpdateAsync();
            return;
        }

        _updateBusy = true;
        SettingsUpdateButton.Content = "Checking…";
        try
        {
            var info = await _updates.CheckAsync();
            if (info is null)
            {
                SettingsUpdateButton.Content = "Up to date";
            }
            else if (info.IsZipPatch)
            {
                _downloadingProgress = true;
                var progress = new Progress<double>(p =>
                {
                    if (_downloadingProgress)
                        SettingsUpdateButton.Content = $"Downloading… {p * 100:0}%";
                });
                await _updates.DownloadAndStageAsync(info, progress);
                _downloadingProgress = false;
                ShowUpdateReady(info);
            }
            else
            {
                ShowUpdateAvailable(info);
            }
        }
        catch
        {
            SettingsUpdateButton.Content = "Check failed — retry";
        }
        finally
        {
            _downloadingProgress = false;
            _updateBusy = false;
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (_updates is null || _pendingUpdate is null || _updateBusy)
            return;

        if (_updateStaged)
        {
            // Swap script waits for this process to exit, patches the install
            // dir, and relaunches the updated app
            _updates.RestartToApply();
            ExitApp();
            return;
        }

        // Installer fallback: download, hand off, exit so it can replace our files
        _updateBusy = true;
        void SetStatus(string text)
        {
            UpdateBannerButton.Content = text;
            SettingsUpdateButton.Content = text;
        }
        _downloadingProgress = true;
        var progress = new Progress<double>(p =>
        {
            if (_downloadingProgress)
                SetStatus($"Downloading… {p * 100:0}%");
        });
        try
        {
            string installer = await _updates.DownloadAsync(_pendingUpdate, progress);
            _downloadingProgress = false;
            SetStatus("Installing…");
            _updates.LaunchInstaller(installer);
            ExitApp();
        }
        catch
        {
            _downloadingProgress = false;
            SetStatus("Update failed — retry");
            _updateBusy = false;
        }
    }

    public void ShowAndActivate()
    {
        // Snapshots were skipped while hidden — show current data immediately
        if (_pump != null)
            _vm.Apply(_pump.Current);
        // A settings page left open across hide-to-tray shows stale control
        // state (and would commit it back on the next interaction)
        if (SettingsOverlay.IsVisible)
            PopulateSettings();
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
