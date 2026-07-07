using Avalonia;
using PCStatsMonitor.App.Bootstrapping;

namespace PCStatsMonitor.App;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Discord-style pending update: if a newer version was downloaded and
        // staged last session, apply it now instead of starting stale — the
        // swap script waits for this process to exit, patches the install
        // directory, and relaunches the updated app.
        if (UpdateService.TryApplyPendingUpdate())
            return;

        UpdateService.SyncInstalledVersionRegistry();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

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
