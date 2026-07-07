using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PCStatsMonitor.App.Bootstrapping;

public sealed record UpdateInfo(Version Version, string TagName, string AssetName, string DownloadUrl, bool IsZipPatch);

/// <summary>
/// Discord-style silent self-update. Preferred path: the release's win-x64 zip
/// asset is downloaded in the background and extracted to a staging directory;
/// a hidden cmd script then swaps the files after the app exits (on "Restart
/// now", or automatically at the next launch via TryApplyPendingUpdate) and
/// relaunches through the elevated launcher task — no installer UI at all.
/// The app runs as admin, so it can patch its own Program Files directory.
/// Releases without a zip asset fall back to downloading and running the
/// installer (whose old uninstaller wipes %LocalAppData%\PCStatsMonitor —
/// hence the settings backup; the zip path never touches settings).
/// </summary>
public sealed class UpdateService
{
    // Releases are published by CI on the RevvLabs remote (tag v<Version>,
    // assets PCStatsMonitor-<Version>-win-x64.zip + PCStatsMonitor-Setup-<Version>.exe)
    private const string RepoSlug = "RevvLabs/PCStatsMonitor";
    private const string LauncherTask = "PCStatsMonitorLauncher";
    // Inno registers its uninstall key as <AppId>_is1 — keep in sync with the .iss
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B7E7A3D1-4C22-4E8B-9A67-PCSTATSMON01}_is1";

    /// <summary>Assembly version comes from &lt;Version&gt; in build/Directory.Build.props.</summary>
    public static Version CurrentVersion { get; } =
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    // Staged under %LocalAppData%\PCStatsMonitor\update\<version>\ — transient,
    // removed by the swap script (and by the uninstaller's LocalAppData wipe)
    private static string StageRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCStatsMonitor", "update");

    /// <summary>Returns the newer release, or null when up to date / tag unparsable.
    /// Prefers the zip asset (silent patch); falls back to the installer exe.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromSeconds(20));
        string json = await http.GetStringAsync(
            $"https://api.github.com/repos/{RepoSlug}/releases/latest", ct);

        using var doc = JsonDocument.Parse(json);
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            return null;

        // Assembly versions carry a 4th (revision) component the tag never has —
        // compare on major.minor.build only
        if (Normalize(latest) <= Normalize(CurrentVersion))
            return null;

        UpdateInfo? installer = null;
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? string.Empty;
            string url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
            if (url.Length == 0)
                continue;
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return new UpdateInfo(latest, tag, name, url, IsZipPatch: true);
            if (installer is null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                installer = new UpdateInfo(latest, tag, name, url, IsZipPatch: false);
        }
        return installer;
    }

    /// <summary>Downloads the zip and extracts it into the staging directory,
    /// ready for the swap. Progress is 0–1 over the download.</summary>
    public async Task DownloadAndStageAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        string zipPath = await DownloadAsync(info, progress, ct);
        string stageDir = Path.Combine(StageRoot, info.Version.ToString(3));
        try
        {
            if (Directory.Exists(StageRoot))
                Directory.Delete(StageRoot, recursive: true); // old partial stages
            Directory.CreateDirectory(stageDir);
            ZipFile.ExtractToDirectory(zipPath, stageDir);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    /// <summary>True when a staged update is waiting to be applied.</summary>
    public bool HasStagedUpdate() => FindStagedUpdate() is not null;

    /// <summary>
    /// Called first thing in Main. If a newer staged update exists, spawns the
    /// swap script and returns true — the caller must exit immediately; the
    /// script waits for this process to die, copies the staged files over the
    /// install, and relaunches the app. Returns false when there is nothing to
    /// apply (the normal launch path).
    /// </summary>
    public static bool TryApplyPendingUpdate()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        string? staged = FindStagedUpdate();
        if (staged is null)
            return false;
        LaunchSwapScript(staged);
        return true;
    }

    /// <summary>"Restart now": spawn the swap script, then the caller exits the app.</summary>
    public void RestartToApply()
    {
        string? staged = FindStagedUpdate();
        if (staged is not null)
            LaunchSwapScript(staged);
    }

    /// <summary>Installer fallback for releases that carry no zip asset.
    /// The caller must exit the app right after. cmd wrapper because the
    /// installer's own post-install relaunch entry is skipifsilent.</summary>
    public void LaunchInstaller(string installerPath)
    {
        AppSettings.BackupForUpdate();
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{installerPath}\" /SILENT /NORESTART & schtasks /run /tn {LauncherTask}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>Downloads the release asset to %TEMP%; progress is 0–1.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        string path = Path.Combine(Path.GetTempPath(), info.AssetName);
        using var http = NewClient(Timeout.InfiniteTimeSpan);
        using var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (total > 0)
                progress?.Report((double)done / total);
        }
        return path;
    }

    /// <summary>
    /// The zip patch bypasses the installer, so Programs &amp; Features would keep
    /// showing the old version forever. Best-effort registry self-heal at startup.
    /// </summary>
    public static void SyncInstalledVersionRegistry()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKey, writable: true);
            if (key is null)
                return; // not an installed copy (dev build) — nothing to heal
            string version = CurrentVersion.ToString(3);
            if (key.GetValue("DisplayVersion") as string != version)
                key.SetValue("DisplayVersion", version);
        }
        catch
        {
            // Cosmetic only — never worth failing startup over.
        }
    }

    /// <summary>Staged dir whose version is strictly newer than what's running;
    /// stale/older stages are deleted so a failed or applied update can't loop.</summary>
    private static string? FindStagedUpdate()
    {
        try
        {
            if (!Directory.Exists(StageRoot))
                return null;
            foreach (string dir in Directory.GetDirectories(StageRoot))
            {
                if (Version.TryParse(Path.GetFileName(dir), out var v)
                    && Normalize(v) > Normalize(CurrentVersion)
                    && File.Exists(Path.Combine(dir, "PCStatsMonitor.exe")))
                    return dir;
            }
            Directory.Delete(StageRoot, recursive: true);
        }
        catch { }
        return null;
    }

    private static void LaunchSwapScript(string stagedDir)
    {
        string appDir = AppContext.BaseDirectory.TrimEnd('\\');
        string script = Path.Combine(Path.GetTempPath(), "PCStatsMonitor-update.cmd");
        int pid = Environment.ProcessId;

        // ping is the sleep: `timeout` refuses to run without an interactive
        // console. schtasks relaunches the installed copy elevated with no UAC;
        // direct exe start is the dev-build fallback.
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
        sb.AppendLine("if not errorlevel 1 (ping -n 2 127.0.0.1 >nul & goto wait)");
        sb.AppendLine($"robocopy \"{stagedDir}\" \"{appDir}\" /E /R:10 /W:1 >nul");
        sb.AppendLine($"rd /s /q \"{StageRoot}\" >nul 2>nul");
        sb.AppendLine($"schtasks /run /tn {LauncherTask} >nul 2>nul || start \"\" \"{appDir}\\PCStatsMonitor.exe\"");
        sb.AppendLine("del \"%~f0\"");
        File.WriteAllText(script, sb.ToString());

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{script}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        // GitHub's API rejects requests without a User-Agent
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PCStatsMonitor");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
