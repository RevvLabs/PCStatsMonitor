using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Linux;

/// <summary>
/// CPU power via Intel RAPL (Running Average Power Limit) sysfs interface.
/// /sys/class/powercap/intel-rapl:0/energy_uj — reads cumulative energy in microjoules.
/// Computes wattage by differencing two readings over the elapsed time.
/// Requires read access to the powercap files (usually no root needed on modern kernels).
/// IsThreadSafe = false: uses state fields for delta computation.
/// </summary>
public sealed class RaplProvider : ISensorProvider
{
    private readonly ILogger<RaplProvider> _log;
    private const string EnergyPath = "/sys/class/powercap/intel-rapl:0/energy_uj";

    private long _prevEnergy;
    private long _prevTicks;
    private double _cpuPower;
    private bool _available;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "RAPL (CPU Power)",
        IsThreadSafe = false,
        SupportedMetrics = new HashSet<MetricKind> { MetricKind.CpuPower },
    };

    public RaplProvider(ILogger<RaplProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        if (!File.Exists(EnergyPath))
        {
            _log.LogInformation("RAPL energy file not found — CPU power provider disabled");
            return ValueTask.CompletedTask;
        }

        if (!TrySampleEnergy(out _prevEnergy))
        {
            _log.LogWarning("RAPL energy file not readable — check permissions");
            return ValueTask.CompletedTask;
        }

        _prevTicks = DateTime.UtcNow.Ticks;
        _available = true;
        _log.LogInformation("RAPL CPU power provider initialized");
        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;
        if (!TrySampleEnergy(out long energy)) return;

        long nowTicks = DateTime.UtcNow.Ticks;
        long deltaTicks = nowTicks - _prevTicks;
        long deltaUj    = energy - _prevEnergy;

        if (deltaTicks > 0 && deltaUj >= 0)
        {
            // W = μJ / μs  (1 μJ/μs = 1 W)
            double elapsedUs = deltaTicks / (double)TimeSpan.TicksPerMicrosecond;
            _cpuPower = deltaUj / elapsedUs;
        }

        _prevEnergy = energy;
        _prevTicks  = nowTicks;
    }

    private static bool TrySampleEnergy(out long value)
    {
        value = 0;
        try
        {
            return long.TryParse(File.ReadAllText(EnergyPath).Trim(), out value);
        }
        catch { return false; }
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available || kind != MetricKind.CpuPower) { value = 0; return false; }
        value = _cpuPower;
        return true;
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
