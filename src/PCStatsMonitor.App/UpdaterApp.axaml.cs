using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PCStatsMonitor.App.Views;

namespace PCStatsMonitor.App;

/// <summary>
/// Minimal Avalonia application for updater mode — no pump, tray, or
/// single-instance guard, just the themed <see cref="UpdaterWindow"/>. The
/// parameters are set as statics before Start because Avalonia constructs the
/// Application itself.
/// </summary>
public class UpdaterApp : Application
{
    public static string InstallDir = "";
    public static int OutgoingPid;
    public static string StageDir = "";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new UpdaterWindow(InstallDir, OutgoingPid, StageDir);
            desktop.MainWindow = window;
            window.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
