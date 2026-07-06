using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PCStatsMonitor.App.Bootstrapping;
using PCStatsMonitor.App.Tray;
using PCStatsMonitor.App.ViewModels;
using PCStatsMonitor.App.Views;
using PCStatsMonitor.Core;
using PCStatsMonitor.Core.Logging;
using PCStatsMonitor.Providers.Lhm;
using Serilog;
using Serilog.Extensions.Logging;

namespace PCStatsMonitor.App;

public class App : Application
{
    private SensorPump? _pump;
    private TrayController? _tray;
    private OverlayWindow? _overlay;
    private SingleInstanceGuard? _guard;
#if WINDOWS
    private PCStatsMonitor.Providers.Windows.FpsMonitor? _fps;
#endif
    private IServiceProvider? _services;
    private Avalonia.Threading.DispatcherTimer? _trimTimer;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Parse args
            bool noTray = desktop.Args?.Contains("--no-tray") ?? false;

            // Single-instance check
            _guard = new SingleInstanceGuard();
            _guard.Initialize();
            if (!_guard.IsFirstInstance)
            {
                // Second instance signaled the first — exit the process directly.
                // desktop.Shutdown() here finishes the dispatcher BEFORE Main enters
                // MainLoop, which then throws "Dispatcher shut down" and crashes.
                _guard.Dispose();
                Environment.Exit(0);
            }

            // User preferences — loaded before DI so potato mode can silence logging
            var settings = AppSettings.Load();

            // Build DI container
            var services = new ServiceCollection();
            ConfigureServices(services, quietLogging: settings.PotatoMode);
            _services = services.BuildServiceProvider();

            // Initialize logging
            AppLog.Initialize(_services.GetRequiredService<ILoggerFactory>());

            // Compose providers
            var providers = ProviderFactory.Create(_services);

            // Build pump
            _pump = new SensorPump(
                providers,
                new CadencePolicy(),
                AppLog.For<SensorPump>());

            // Build UI
            var vm = new MainViewModel();
            var window = new MainWindow(vm, _pump, settings);
            desktop.MainWindow = window;

            // Load shared icon once and assign to window (and pass to tray)
            WindowIcon? appIcon = null;
            try
            {
                using var iconStream = AssetLoader.Open(new Uri("avares://PCStatsMonitor/Assets/tray-icon.ico"));
                appIcon = new WindowIcon(iconStream);
                window.Icon = appIcon;
            }
            catch { /* ignore - icon is optional */ }

            // ShowAndActivate, not Show: the launcher scheduled task spawns us from the
            // Task Scheduler service, so Windows denies normal foreground activation and
            // the window would open at the bottom of the z-order. The Topmost flip inside
            // forces it to front.
            window.ShowAndActivate();

            // Tray — icon lives only while the close preference keeps the app in the
            // tray (Ask counts: the first-close prompt may choose tray, and the icon
            // must already exist when the window hides). Chosen Exit → no icon.
            _tray = new TrayController();
            _tray.Initialize(window, !noTray, settings.CloseBehavior != CloseBehavior.Exit, appIcon, settings);

            // Desktop HUD overlay — created/destroyed to follow the setting. The pump
            // pauses when the main window hides, so an active overlay must keep it
            // polling (WindowVisible is the pump's "anyone watching?" flag).
            void SyncOverlay()
            {
                // FPS monitor: an ETW trace session lives only while the FPS
                // readout is enabled — it has real CPU/RAM cost. Needs admin;
                // Start() failing silently leaves the readout at "—".
#if WINDOWS
                string? ReadFpsText()
                {
                    if (!settings.ShowOverlay || !settings.OverlayShowFps)
                        return null;
                    return _fps?.ReadForegroundFpsText();
                }
#endif
#if WINDOWS
                bool wantFps = settings.ShowOverlay && settings.OverlayShowFps;
                if (wantFps && _fps is null)
                {
                    _fps = new PCStatsMonitor.Providers.Windows.FpsMonitor();
                    if (!_fps.Start())
                    {
                        _fps.Dispose();
                        _fps = null;
                    }
                }
                else if (!wantFps && _fps is not null)
                {
                    _fps.Dispose();
                    _fps = null;
                }
#endif

                if (settings.ShowOverlay && _overlay is null)
                {
#if WINDOWS
                    _overlay = new OverlayWindow(_pump, settings, ReadFpsText);
#else
                    _overlay = new OverlayWindow(_pump, settings);
#endif
                    _overlay.Show();
                }
                else if (!settings.ShowOverlay && _overlay is not null)
                {
                    _overlay.Close();
                    _overlay = null;
                }
                else
                {
                    // Still open — position/size settings may have changed
                    _overlay?.ApplySettings();
                }
                if (_pump != null)
                {
                    bool mainVisible = window.IsVisible && window.WindowState != WindowState.Minimized;
                    _pump.WindowVisible = mainVisible || settings.ShowOverlay;
                }
            }
            SyncOverlay();

            settings.Changed += () =>
            {
                _tray?.SetEnabled(settings.CloseBehavior != CloseBehavior.Exit);
                _tray?.RefreshOverlayCheck();
                SyncOverlay();
            };

            _guard.ShowWindowRequested += () =>
                Avalonia.Threading.Dispatcher.UIThread.Post(window.ShowAndActivate);

            // Periodic working-set maintenance: startup leaves ~150 MB of one-time
            // init pages resident, and re-touched pages creep back after a trim.
            // Trimmed pages land on the OS standby list, so re-faults are soft
            // (no disk I/O) — repeating this stays cheap. Potato mode trims far
            // more aggressively to hold the resident set down continuously.
            _trimTimer = new Avalonia.Threading.DispatcherTimer(
                Avalonia.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMinutes(settings.PotatoMode ? 0.75 : 1),
            };
            _trimTimer.Tick += (_, _) => WorkingSetTrimmer.Trim();
            _trimTimer.Start();
            settings.Changed += () =>
                _trimTimer.Interval = TimeSpan.FromMinutes(settings.PotatoMode ? 0.75 : 1);

            // Don't wait for the first periodic tick — startup leaves ~150 MB of
            // one-time init pages resident; evict them right away.
            var earlyTrim = new Avalonia.Threading.DispatcherTimer(
                Avalonia.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(10),
            };
            earlyTrim.Tick += (s, _) =>
            {
                ((Avalonia.Threading.DispatcherTimer)s!).Stop();
                WorkingSetTrimmer.Trim();
            };
            earlyTrim.Start();

            // Start pump in background (async fire-and-forget)
            _ = Task.Run(async () =>
            {
                await _pump.StartAsync();
                Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
                    AppLog.ForContext("Bootstrap"),
                    "SensorPump started with {Count} providers", providers.Count);
            });

            desktop.Exit += (_, _) =>
            {
                _pump?.Dispose();
#if WINDOWS
                _fps?.Dispose(); // stops the ETW session — must not outlive the process
#endif
                _tray?.Dispose();
                _guard?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services, bool quietLogging = false)
    {
        // Serilog → MEL bridge. Potato mode: fully silent — no sinks, no file,
        // messages never built. Diagnosing potato-machine issues means toggling
        // potato off (or a Debug build); that trade is deliberate.
        var config = new LoggerConfiguration();
        if (quietLogging)
        {
            config.MinimumLevel.Fatal();
        }
        else
        {
            config
#if DEBUG
                .MinimumLevel.Debug()
#else
                // Debug-level logging allocates strings every sensor tick — needless
                // GC churn + file I/O in production builds
                .MinimumLevel.Information()
#endif
                .WriteTo.File(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PCStatsMonitor", "logs", "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 5);
#if DEBUG
            config.WriteTo.Debug();
#endif
        }
        var serilog = config.CreateLogger();

        services.AddLogging(lb => lb.AddSerilog(serilog, dispose: true));

        // LHM provider registered as scoped singleton
        services.AddSingleton<LhmSensorProvider>();
    }
}
