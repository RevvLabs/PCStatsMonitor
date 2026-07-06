using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Linux;

/// <summary>
/// CPU temperature and fan speed from /sys/class/hwmon.
/// Handles coretemp (Intel), k10temp (AMD — Tctl/Tdie labels), nct6775 (SuperIO fans).
/// IsThreadSafe = false: file paths are resolved at init; readings are sequential.
/// </summary>
public sealed class HwmonProvider : ISensorProvider
{
    private readonly ILogger<HwmonProvider> _log;

    private string? _cpuTempPath;
    private string? _cpuFanPath;
    private double _cpuTemp;
    private double _cpuFan;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "hwmon (Linux temp/fan)",
        IsThreadSafe = false,
        SupportedMetrics = new HashSet<MetricKind> { MetricKind.CpuTempPackage, MetricKind.CpuFanRpm },
    };

    public HwmonProvider(ILogger<HwmonProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        const string hwmonBase = "/sys/class/hwmon";
        if (!Directory.Exists(hwmonBase))
        {
            _log.LogInformation("hwmon not available — temperature/fan provider disabled");
            return ValueTask.CompletedTask;
        }

        foreach (var hwmon in Directory.GetDirectories(hwmonBase))
        {
            string namePath = Path.Combine(hwmon, "name");
            if (!File.Exists(namePath)) continue;

            string driverName = File.ReadAllText(namePath).Trim();

            // CPU temperature: coretemp or k10temp
            if (driverName is "coretemp" or "k10temp" && _cpuTempPath == null)
                _cpuTempPath = FindCpuTempPath(hwmon);

            // CPU fan: look for nct6775/nct6776/nct6779 (SuperIO) or "asus" EC fan
            if (driverName.StartsWith("nct", StringComparison.Ordinal) && _cpuFanPath == null)
                _cpuFanPath = FindCpuFanPath(hwmon);
        }

        _log.LogInformation("hwmon: temp={TempPath} fan={FanPath}", _cpuTempPath ?? "N/A", _cpuFanPath ?? "N/A");
        return ValueTask.CompletedTask;
    }

    private static string? FindCpuTempPath(string hwmon)
    {
        // Preferred labels in priority order
        string[] preferredLabels = { "Package id 0", "Tdie", "Tctl", "CPU Temperature" };

        foreach (var label in preferredLabels)
        {
            foreach (var labelFile in Directory.GetFiles(hwmon, "temp*_label"))
            {
                if (File.ReadAllText(labelFile).Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
                {
                    // Corresponding input file: temp1_label → temp1_input
                    string inputFile = labelFile.Replace("_label", "_input");
                    if (File.Exists(inputFile)) return inputFile;
                }
            }
        }

        // Fallback: first temp*_input
        return Directory.GetFiles(hwmon, "temp*_input").FirstOrDefault();
    }

    private static string? FindCpuFanPath(string hwmon)
    {
        // Look for a fan labeled "CPU FAN" first
        foreach (var labelFile in Directory.GetFiles(hwmon, "fan*_label"))
        {
            string label = File.ReadAllText(labelFile).Trim();
            if (label.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            {
                string inputFile = labelFile.Replace("_label", "_input");
                if (File.Exists(inputFile)) return inputFile;
            }
        }
        return Directory.GetFiles(hwmon, "fan*_input").FirstOrDefault();
    }

    public void Refresh(MetricGroup group)
    {
        if (_cpuTempPath != null && File.Exists(_cpuTempPath))
        {
            // hwmon temp values are in millidegrees Celsius
            if (long.TryParse(File.ReadAllText(_cpuTempPath).Trim(), out long raw))
                _cpuTemp = raw / 1000.0;
        }

        if (_cpuFanPath != null && File.Exists(_cpuFanPath))
        {
            if (double.TryParse(File.ReadAllText(_cpuFanPath).Trim(), out double rpm))
                _cpuFan = rpm;
        }
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        value = kind switch
        {
            MetricKind.CpuTempPackage => _cpuTemp,
            MetricKind.CpuFanRpm      => _cpuFan,
            _                         => 0,
        };
        return (kind == MetricKind.CpuTempPackage && _cpuTempPath != null && _cpuTemp > 0)
            || (kind == MetricKind.CpuFanRpm      && _cpuFanPath  != null);
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
