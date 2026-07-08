using System.Diagnostics;

namespace PCStatsMonitor.App.Bootstrapping;

/// <summary>
/// Toggles "start with Windows" by enabling/disabling the logon TRIGGER of the
/// installer-created launcher task (PCStatsMonitorLauncher, RunLevel=Highest).
///
/// The task itself must stay enabled — a disabled task refuses manual
/// <c>schtasks /run</c> too, which the tray/shortcuts rely on. schtasks.exe can
/// only enable/disable the whole task, not a single trigger, so the flip goes
/// through a PowerShell one-liner (same approach the installer uses). The app
/// runs elevated, so the child PowerShell inherits the rights Set-ScheduledTask
/// needs. On dev builds where the task was never installed, every call no-ops.
/// </summary>
internal static class StartupTask
{
    private const string TaskName = "PCStatsMonitorLauncher";

    /// <summary>Reconcile the actual task trigger to the desired state, off the
    /// UI thread. Only writes when it differs, so it's cheap to call on every
    /// settings save. Silent on any failure (not installed, not elevated).</summary>
    public static void Sync(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return;

        Task.Run(() =>
        {
            try
            {
                bool? current = GetTriggerEnabled();
                if (current is null || current == enabled)
                    return; // task absent (dev build) or already in the wanted state
                SetTriggerEnabled(enabled);
            }
            catch
            {
                // Best-effort — never surface a scheduler hiccup to the user.
            }
        });
    }

    /// <summary>True/false when the launcher task exists, null when it doesn't.</summary>
    private static bool? GetTriggerEnabled()
    {
        string output = RunPowerShell(
            $"try {{ (Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction Stop).Triggers[0].Enabled }} " +
            "catch { '' }");
        return bool.TryParse(output.Trim(), out bool value) ? value : null;
    }

    private static void SetTriggerEnabled(bool enabled)
    {
        string flag = enabled ? "$true" : "$false";
        RunPowerShell(
            $"$t = Get-ScheduledTask -TaskName '{TaskName}'; " +
            $"$t.Triggers[0].Enabled = {flag}; " +
            "Set-ScheduledTask -InputObject $t | Out-Null");
    }

    private static string RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null)
            return string.Empty;
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(8000);
        return stdout;
    }
}
