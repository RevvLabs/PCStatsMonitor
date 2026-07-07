using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCStatsMonitor.App.Bootstrapping;

public enum CloseBehavior
{
    /// <summary>No preference stored yet — prompt the user on close.</summary>
    Ask,
    MinimizeToTray,
    Exit,
}

public enum OverlaySize
{
    Small,
    Medium,
    Large,
}

public enum OverlayCorner
{
    Pill,
    Rounded,
    Rectangle,
}

public enum OverlayBackground
{
    Dark,
    Black,
    Slate,
    /// <summary>No plate at all — floating text, NVIDIA-overlay style.</summary>
    Invisible,
}

public enum OverlayPosition
{
    TopRight,
    TopCenter,
    TopLeft,
    // Bottom rows dock just above the taskbar (positions use the working area,
    // which excludes it)
    BottomRight,
    BottomCenter,
    BottomLeft,
}

/// <summary>
/// User preferences persisted as JSON under %LocalAppData%\PCStatsMonitor.
/// The uninstaller already removes that directory, so no extra cleanup is needed.
/// </summary>
public sealed class AppSettings
{
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Ask;

    /// <summary>Desktop HUD overlay (click-through pill) on/off.</summary>
    public bool ShowOverlay { get; set; }

    /// <summary>Screen corner the overlay pill docks to.</summary>
    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.TopRight;

    /// <summary>Overlay pill scale.</summary>
    public OverlaySize OverlaySize { get; set; } = OverlaySize.Medium;

    // Which metric groups the overlay pill shows
    public bool OverlayShowCpu { get; set; } = true;
    public bool OverlayShowGpu { get; set; } = true;
    public bool OverlayShowRam { get; set; } = true;
    public bool OverlayShowDisk { get; set; }

    /// <summary>Append temperatures to load values (e.g. "7% · 45°C").</summary>
    public bool OverlayShowTemps { get; set; } = true;

    /// <summary>Base font size of overlay values; labels scale down from it.</summary>
    public double OverlayFontSize { get; set; } = 10.5;

    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.Pill;
    public OverlayBackground OverlayBackground { get; set; } = OverlayBackground.Dark;

    /// <summary>Plate opacity 0–1; 0 hides the plate entirely (same as Invisible).</summary>
    public double OverlayOpacity { get; set; } = 0.9;

    /// <summary>Pixel gap from the top/bottom screen edge (whichever the position docks to).</summary>
    public int OverlayOffsetY { get; set; } = 16;

    /// <summary>Pixel gap from the left/right screen edge; ignored for center positions.</summary>
    public int OverlayOffsetX { get; set; } = 16;

    /// <summary>Kills all animations (gauge spring, glow pass) for weak machines.</summary>
    public bool PotatoMode { get; set; }

    /// <summary>Single-tone theme: accent colors collapse to neutral gray.</summary>
    public bool Monotone { get; set; }

    /// <summary>Overlay shows metric icons instead of CPU/GPU/RAM/DISK words.</summary>
    public bool OverlayIcons { get; set; }

    /// <summary>FPS readout of the foreground game (ETW present telemetry;
    /// needs the app's admin rights, costs a trace session while enabled).</summary>
    public bool OverlayShowFps { get; set; }

    /// <summary>Index into the overlay font list (Inter, Segoe UI, Consolas,
    /// Bahnschrift, Arial, Cascadia Mono, Verdana).</summary>
    public int OverlayFontIndex { get; set; }

    /// <summary>Raised after Save(); used to toggle the tray icon live. Events are
    /// never serialized, so no JsonIgnore is needed.</summary>
    public event Action? Changed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCStatsMonitor", "settings.json");

    // The installer's uninstall-first upgrade wipes %LocalAppData%\PCStatsMonitor,
    // so an in-app update stashes settings in %TEMP% and Load() restores them on
    // the first start of the new version.
    private static string BackupPath => Path.Combine(
        Path.GetTempPath(), "PCStatsMonitor-settings-backup.json");

    /// <summary>Called by UpdateService right before handing off to the installer.</summary>
    public static void BackupForUpdate()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Copy(FilePath, BackupPath, overwrite: true);
        }
        catch
        {
            // Losing settings across an update is annoying but never blocks it.
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath) && File.Exists(BackupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(BackupPath, FilePath);
                File.Delete(BackupPath);
            }
        }
        catch
        {
            // Restore is best-effort; fall through to a normal load.
        }
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions)
                       ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings must never block startup — fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Failing to persist is non-fatal; the in-memory value still applies this session.
        }
        Changed?.Invoke();
    }
}
