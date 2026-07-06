using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Linux;

/// <summary>
/// RAM usage from /proc/meminfo. No root required.
/// IsThreadSafe = true: reads an atomic proc snapshot.
/// </summary>
public sealed class MeminfoProvider : ISensorProvider
{
    private readonly ILogger<MeminfoProvider> _log;
    private double _usedBytes, _totalBytes, _loadPct;
    private bool _available;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "/proc/meminfo (RAM)",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind>
        {
            MetricKind.RamUsedBytes,
            MetricKind.RamTotalBytes,
            MetricKind.RamLoadPct,
        },
    };

    public MeminfoProvider(ILogger<MeminfoProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        _available = File.Exists("/proc/meminfo");
        if (!_available) _log.LogInformation("/proc/meminfo not found — RAM provider disabled");
        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;

        long totalKb = 0, availableKb = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    totalKb = ParseKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    availableKb = ParseKb(line);

                if (totalKb > 0 && availableKb > 0) break;
            }

            if (totalKb > 0)
            {
                long usedKb = totalKb - availableKb;
                _totalBytes = totalKb * 1024.0;
                _usedBytes  = usedKb  * 1024.0;
                _loadPct    = (double)usedKb / totalKb * 100.0;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "/proc/meminfo read failed");
        }
    }

    private static long ParseKb(string line)
    {
        // "MemTotal:       16384000 kB"
        var parts = line.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return 0;
        return long.TryParse(parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out long v) ? v : 0;
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        value = kind switch
        {
            MetricKind.RamUsedBytes  => _usedBytes,
            MetricKind.RamTotalBytes => _totalBytes,
            MetricKind.RamLoadPct    => _loadPct,
            _                        => 0,
        };
        return kind is MetricKind.RamUsedBytes or MetricKind.RamTotalBytes or MetricKind.RamLoadPct
               && _totalBytes > 0 && _available;
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
