using Avalonia;
using PCStatsMonitor.App.Bootstrapping;

namespace PCStatsMonitor.App;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Updater mode: this staged (new) exe was launched to apply the update.
        // Show the themed progress window and swap the files — never proceed to
        // normal startup, and never re-trigger the pending-update check.
        int applyIdx = System.Array.IndexOf(args, UpdateService.ApplyUpdateArg);
        if (applyIdx >= 0 && applyIdx + 1 < args.Length)
        {
            UpdaterApp.InstallDir = args[applyIdx + 1];
            UpdaterApp.OutgoingPid = applyIdx + 2 < args.Length
                && int.TryParse(args[applyIdx + 2], out int pid) ? pid : 0;
            UpdaterApp.StageDir = AppContext.BaseDirectory.TrimEnd('\\');
            BuildUpdaterApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        // Discord-style pending update: if a newer version was downloaded and
        // staged last session, apply it now instead of starting stale — this
        // launches the updater window (staged exe) and exits so its files can
        // be patched, then the updated app relaunches.
        if (UpdateService.TryApplyPendingUpdate())
            return;

        UpdateService.SyncInstalledVersionRegistry();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildUpdaterApp() =>
        AppBuilder.Configure<UpdaterApp>()
            .UsePlatformDetect()
            .WithInterFont();

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        // Potato mode: software rendering instead of Skia-on-D3D — drops the
        // GPU swapchain/texture allocations (the largest single RAM chunk).
        // Renderer is fixed at startup, so toggling takes effect on next launch.
        if (AppSettings.Load().PotatoMode)
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software },
            });

        return builder;
    }
}
