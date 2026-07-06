using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using PCStatsMonitor.App.Bootstrapping;
using PCStatsMonitor.App.Views;

namespace PCStatsMonitor.App.Tray;

public sealed class TrayController : IDisposable
{
    private TrayIcon? _trayIcon;
    private MainWindow? _window;
    private WindowIcon? _icon;
    private bool _allowed;
    private AppSettings? _settings;
    private NativeMenuItem? _overlayItem;

    /// <param name="allowed">False for --no-tray: the icon can never be shown.</param>
    /// <param name="showIcon">Whether the icon should be visible right now.</param>
    public void Initialize(MainWindow window, bool allowed, bool showIcon, WindowIcon? sharedIcon = null, AppSettings? settings = null)
    {
        _window = window;
        _allowed = allowed;
        _settings = settings;

        if (!allowed)
            return;

        // Prefer the shared WindowIcon if provided, otherwise attempt to load locally.
        _icon = sharedIcon;
        if (_icon is null)
        {
            try
            {
                using var iconStream = AssetLoader.Open(new Uri("avares://PCStatsMonitor/Assets/tray-icon.ico"));
                _icon = new WindowIcon(iconStream);
            }
            catch
            {
                // Asset load failures should not prevent app startup.
            }
        }

        if (showIcon)
            CreateIcon();
    }

    /// <summary>
    /// Show or remove the tray icon at runtime — the icon only exists while the
    /// user's close-behavior preference wants the app living in the tray.
    /// Must be called on the UI thread. No-op if Initialize was told tray is
    /// disallowed entirely (--no-tray).
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (!_allowed && enabled)
            return;
        if (enabled && _trayIcon is null)
            CreateIcon();
        else if (!enabled && _trayIcon is not null)
            DestroyIcon();
    }

    private void CreateIcon()
    {
        _trayIcon = new TrayIcon
        {
            Icon = _icon,
            ToolTipText = "PC Stats Monitor",
            IsVisible = true,
            Menu = BuildMenu()
        };

        // ShowAndActivate: plain Show()+Activate() can't take foreground when the
        // process was spawned by the Task Scheduler service (launcher task).
        _trayIcon.Clicked += (_, _) => _window?.ShowAndActivate();
    }

    /// <summary>Keeps the menu checkbox in step when the setting changes elsewhere
    /// (main-window settings panel).</summary>
    public void RefreshOverlayCheck()
    {
        if (_overlayItem is not null && _settings is not null)
            _overlayItem.IsChecked = _settings.ShowOverlay;
    }

    private void DestroyIcon()
    {
        if (_trayIcon is null)
            return;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("Show Monitor");
        showItem.Click += (_, _) => _window?.ShowAndActivate();

        menu.Add(showItem);

        if (_settings is not null)
        {
            _overlayItem = new NativeMenuItem("Desktop Overlay")
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = _settings.ShowOverlay,
            };
            _overlayItem.Click += (_, _) =>
            {
                _settings.ShowOverlay = !_settings.ShowOverlay;
                _settings.Save(); // App's Changed handler creates/destroys the overlay
                _overlayItem.IsChecked = _settings.ShowOverlay;
            };
            menu.Add(_overlayItem);
        }

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        menu.Add(exitItem);

        return menu;
    }

    private void ExitApplication()
    {
        if (_trayIcon is not null)
            _trayIcon.IsVisible = false;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    public void Dispose() => DestroyIcon();
}
