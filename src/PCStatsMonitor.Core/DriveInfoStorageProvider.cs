using Microsoft.Extensions.Logging;

namespace PCStatsMonitor.Core;

/// <summary>
/// Storage space via System.IO.DriveInfo. Fully cross-platform (Windows + Linux).
/// Detects the system drive from the OS directory path — never hardcodes "C:".
/// IsThreadSafe = true: DriveInfo reads are atomic.
/// </summary>
public sealed class DriveInfoStorageProvider : ISensorProvider
{
    private readonly ILogger<DriveInfoStorageProvider> _log;
    private DriveInfo? _systemDrive;
    private double _usedBytes, _totalBytes, _loadPct;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "DriveInfo (Storage Space)",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind>
        {
            MetricKind.StorageUsedBytes,
            MetricKind.StorageTotalBytes,
            MetricKind.StorageLoadPct,
        },
    };

    public DriveInfoStorageProvider(ILogger<DriveInfoStorageProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        try
        {
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            // On Linux SpecialFolder.System returns "" — fall back to root
            if (string.IsNullOrEmpty(sysDir)) sysDir = "/";
            string root = Path.GetPathRoot(sysDir) ?? "/";
            _systemDrive = new DriveInfo(root);
            _log.LogInformation("Storage provider targeting drive: {Root}", root);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DriveInfo storage provider failed to initialize");
        }
        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (_systemDrive == null || !_systemDrive.IsReady) return;
        try
        {
            _totalBytes = _systemDrive.TotalSize;
            _usedBytes  = _totalBytes - _systemDrive.AvailableFreeSpace;
            _loadPct    = _totalBytes > 0 ? (_usedBytes / _totalBytes) * 100.0 : 0;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "DriveInfo refresh failed");
        }
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        value = kind switch
        {
            MetricKind.StorageUsedBytes  => _usedBytes,
            MetricKind.StorageTotalBytes => _totalBytes,
            MetricKind.StorageLoadPct    => _loadPct,
            _                            => 0,
        };
        return kind is MetricKind.StorageUsedBytes or MetricKind.StorageTotalBytes or MetricKind.StorageLoadPct
               && _totalBytes > 0;
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
