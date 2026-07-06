using System;
using System.Runtime.InteropServices;

namespace PCStatsMonitor.App.Bootstrapping;

/// <summary>
/// Trims the process working set after the window hides to the tray.
/// The sensor pump is paused at that point, so released pages are not touched
/// again until the window is shown; Windows pages them back in on demand.
/// </summary>
internal static class WorkingSetTrimmer
{
    public static void Trim()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            // Compact the managed heap first so the pages we release are truly idle
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();

            K32EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            // Best-effort: a failed trim must never take the app down
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    // K32-prefixed PSAPI functions are exported by kernel32.dll (Win7+),
    // NOT by psapi.dll — psapi.dll exports the unprefixed legacy names.
    [DllImport("kernel32.dll")]
    private static extern bool K32EmptyWorkingSet(IntPtr hProcess);
}
