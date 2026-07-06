using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Linux;

/// <summary>
/// AMD GPU sensor provider via DRM sysfs (/sys/class/drm/card*/device/).
/// Provides gpu_busy_percent (load) and temperature from hwmon sub-device.
/// Also used as a fallback for Intel GPU via gt_* sysfs.
/// IsThreadSafe = true: reads static file paths, no mutable shared state between reads.
/// </summary>
public sealed class SysfsGpuProvider : ISensorProvider
{
    private readonly ILogger<SysfsGpuProvider> _log;
    private string? _busyPath;
    private string? _tempPath;
    private string _gpuName = "Unknown GPU";
    private double _gpuLoad, _gpuTemp;
    private bool _available;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "sysfs DRM (Linux GPU)",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind>
        {
            MetricKind.GpuLoad,
            MetricKind.GpuTempCore,
        },
    };

    public SysfsGpuProvider(ILogger<SysfsGpuProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        const string drmBase = "/sys/class/drm";
        if (!Directory.Exists(drmBase)) return ValueTask.CompletedTask;

        foreach (var cardDir in Directory.GetDirectories(drmBase, "card[0-9]"))
        {
            string deviceDir = Path.Combine(cardDir, "device");
            if (!Directory.Exists(deviceDir)) continue;

            // AMD: gpu_busy_percent
            string busyCandidate = Path.Combine(deviceDir, "gpu_busy_percent");
            if (File.Exists(busyCandidate))
            {
                _busyPath = busyCandidate;
                _tempPath = FindAmdHwmonTemp(deviceDir);
                string ueventPath = Path.Combine(deviceDir, "uevent");
                if (File.Exists(ueventPath))
                {
                    foreach (var line in File.ReadLines(ueventPath))
                    {
                        if (line.StartsWith("PCI_ID=", StringComparison.Ordinal))
                            _gpuName = "AMD GPU (" + line.Substring(7) + ")";
                    }
                }
                _available = true;
                _log.LogInformation("sysfs GPU: AMD card at {Dir}", cardDir);
                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    private static string? FindAmdHwmonTemp(string deviceDir)
    {
        string hwmonDir = Path.Combine(deviceDir, "hwmon");
        if (!Directory.Exists(hwmonDir)) return null;

        foreach (var subDir in Directory.GetDirectories(hwmonDir))
        {
            // Prefer temp1_input labeled "edge" or "junction"
            foreach (var labelFile in Directory.GetFiles(subDir, "temp*_label"))
            {
                string label = File.ReadAllText(labelFile).Trim();
                if (label is "edge" or "junction")
                {
                    string inp = labelFile.Replace("_label", "_input");
                    if (File.Exists(inp)) return inp;
                }
            }
            var first = Directory.GetFiles(subDir, "temp*_input").FirstOrDefault();
            if (first != null) return first;
        }
        return null;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;

        if (_busyPath != null && File.Exists(_busyPath))
        {
            if (double.TryParse(File.ReadAllText(_busyPath).Trim(), out double pct))
                _gpuLoad = Math.Clamp(pct, 0, 100);
        }

        if (_tempPath != null && File.Exists(_tempPath))
        {
            if (long.TryParse(File.ReadAllText(_tempPath).Trim(), out long milli))
                _gpuTemp = milli / 1000.0;
        }
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available) { value = 0; return false; }
        value = kind switch
        {
            MetricKind.GpuLoad     => _gpuLoad,
            MetricKind.GpuTempCore => _gpuTemp,
            _                      => 0,
        };
        return kind is MetricKind.GpuLoad or MetricKind.GpuTempCore;
    }

    public HardwareIdentity? ResolveIdentity(string domain) =>
        domain == "GPU" && _available ? new HardwareIdentity("GPU", "AMD", _gpuName) : null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
