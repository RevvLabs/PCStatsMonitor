using System.Management;
using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Windows;

/// <summary>
/// CPU package temperature via WMI MSAcpi_ThermalZoneTemperature.
/// Fallback when LHM cannot read CPU temp (e.g. Intel Arrow Lake).
/// Temperature is in tenths of Kelvin; converted to °C.
/// </summary>
public sealed class WmiCpuTempProvider : ISensorProvider
{
    private readonly ILogger<WmiCpuTempProvider> _log;
    private ManagementScope? _scope;
    private string? _instanceName; // WMI-escaped instance name for WHERE clause
    private double _tempC;
    private bool _available;

    private static readonly ConnectionOptions WmiOptions = new()
    {
        Impersonation = ImpersonationLevel.Impersonate,
        Authentication = AuthenticationLevel.PacketPrivacy,
    };

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "WMI CPU Temperature",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind> { MetricKind.CpuTempPackage },
    };

    public WmiCpuTempProvider(ILogger<WmiCpuTempProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI", WmiOptions);
            scope.Connect();

            string? bestZone = null;
            int bestScore = -1;

            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT InstanceName FROM MSAcpi_ThermalZoneTemperature"));

            foreach (ManagementBaseObject obj in searcher.Get())
            {
                using (obj)
                {
                    string name = obj["InstanceName"]?.ToString() ?? "";
                    int score = 0;
                    if (name.Contains("THRM", StringComparison.OrdinalIgnoreCase)) score += 3;
                    if (name.Contains("CPU",  StringComparison.OrdinalIgnoreCase)) score += 5;
                    if (name.Contains("PROC", StringComparison.OrdinalIgnoreCase)) score += 4;
                    if (name.Contains("TZ",   StringComparison.OrdinalIgnoreCase)) score += 1;
                    if (score == 0) score = 1; // fallback: any zone beats nothing

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestZone = name;
                    }
                }
            }

            if (bestZone != null)
            {
                _scope = scope;
                // WQL strings use backslash-escaped single quotes
                _instanceName = bestZone.Replace("\\", "\\\\").Replace("'", "\\'");
                _available = true;
                _log.LogInformation("WMI CPU temp: using thermal zone '{Zone}'", bestZone);
            }
            else
            {
                _log.LogWarning("WMI CPU temp: no MSAcpi_ThermalZoneTemperature zones found");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WMI CPU temp provider failed to initialize");
        }
        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available || _scope == null || _instanceName == null) return;
        try
        {
            using var searcher = new ManagementObjectSearcher(_scope,
                new ObjectQuery($"SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature WHERE InstanceName='{_instanceName}'"));
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                using (obj)
                {
                    uint raw = Convert.ToUInt32(obj["CurrentTemperature"]);
                    _tempC = raw / 10.0 - 273.15;
                }
                break;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "WMI CPU temp read failed");
        }
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available || kind != MetricKind.CpuTempPackage) { value = 0; return false; }
        value = _tempC;
        return _tempC > 0;
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
