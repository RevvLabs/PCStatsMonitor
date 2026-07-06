using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Linux;

/// <summary>
/// CPU total load by diffing /proc/stat between two consecutive readings.
/// This is the canonical Linux CPU load source — no admin required.
/// IsThreadSafe = true: reads an atomic file snapshot; state is local fields written
/// from one thread.
/// </summary>
public sealed class ProcStatCpuProvider : ISensorProvider
{
    private readonly ILogger<ProcStatCpuProvider> _log;
    private long _prevIdle, _prevTotal;
    private double _cpuLoad;
    private bool _available;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "/proc/stat (CPU Load)",
        IsThreadSafe = false, // state mutation is not thread-safe
        SupportedMetrics = new HashSet<MetricKind> { MetricKind.CpuLoad },
    };

    public ProcStatCpuProvider(ILogger<ProcStatCpuProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        if (!File.Exists("/proc/stat"))
        {
            _log.LogInformation("/proc/stat not found — ProcStat CPU provider disabled");
            return ValueTask.CompletedTask;
        }
        Sample(); // prime baseline
        _available = true;
        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;
        Sample();
    }

    private void Sample()
    {
        try
        {
            string? line = null;
            using var reader = new StreamReader("/proc/stat");
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("cpu ", StringComparison.Ordinal))
                    break;
            }
            if (line == null) return;

            // cpu  user nice system idle iowait irq softirq steal guest guest_nice
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return;

            long user    = long.Parse(parts[1]);
            long nice    = long.Parse(parts[2]);
            long system  = long.Parse(parts[3]);
            long idle    = long.Parse(parts[4]);
            long iowait  = parts.Length > 5 ? long.Parse(parts[5]) : 0;
            long irq     = parts.Length > 6 ? long.Parse(parts[6]) : 0;
            long softirq = parts.Length > 7 ? long.Parse(parts[7]) : 0;
            long steal   = parts.Length > 8 ? long.Parse(parts[8]) : 0;

            long totalIdle  = idle + iowait;
            long total      = user + nice + system + idle + iowait + irq + softirq + steal;

            long diffTotal = total - _prevTotal;
            long diffIdle  = totalIdle - _prevIdle;

            if (diffTotal > 0)
                _cpuLoad = Math.Clamp((1.0 - (double)diffIdle / diffTotal) * 100.0, 0, 100);

            _prevTotal = total;
            _prevIdle  = totalIdle;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "/proc/stat read failed");
        }
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available || kind != MetricKind.CpuLoad) { value = 0; return false; }
        value = _cpuLoad;
        return true;
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
