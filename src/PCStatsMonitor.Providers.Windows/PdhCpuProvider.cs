using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Windows;

/// <summary>
/// CPU total load via PDH (Performance Data Helper) API.
/// Used as a fallback when LHM CPU load sensor is unavailable.
/// Uses the more accurate "% Processor Utility" counter (accounts for frequency scaling)
/// rather than "% Processor Time".
/// IsThreadSafe = true: PDH query with a dedicated handle is safe for concurrent reads.
/// </summary>
public sealed class PdhCpuProvider : ISensorProvider
{
    private readonly ILogger<PdhCpuProvider> _log;
    private nint _query;
    private nint _counter;
    private bool _available;
    private double _cpuLoad;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "PDH (CPU fallback)",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind> { MetricKind.CpuLoad },
    };

    public PdhCpuProvider(ILogger<PdhCpuProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        try
        {
            int r = PdhOpenQuery(nint.Zero, nint.Zero, out _query);
            if (r != 0) { _log.LogWarning("PdhOpenQuery failed: 0x{Code:X}", r); return ValueTask.CompletedTask; }

            // "% Processor Utility" is frequency-scaling-aware; fall back to "% Processor Time" if not present
            r = PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", nint.Zero, out _counter);
            if (r != 0)
                r = PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", nint.Zero, out _counter);

            if (r != 0) { _log.LogWarning("PdhAddEnglishCounter failed: 0x{Code:X}", r); return ValueTask.CompletedTask; }

            PdhCollectQueryData(_query); // prime first sample (PDH needs 2 samples to compute rate)
            _available = true;
            _log.LogInformation("PDH CPU provider initialized");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDH CPU provider failed to initialize");
        }
        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;
        PdhCollectQueryData(_query);

        var fmtValue = new PDH_FMT_COUNTERVALUE();
        int r = PdhGetFormattedCounterValue(_counter, PDH_FMT_DOUBLE, nint.Zero, ref fmtValue);
        if (r == 0)
            _cpuLoad = Math.Clamp(fmtValue.doubleValue, 0, 100);
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available || kind != MetricKind.CpuLoad) { value = 0; return false; }
        value = _cpuLoad;
        return true;
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;

    public ValueTask DisposeAsync()
    {
        if (_query != nint.Zero)
            PdhCloseQuery(_query);
        return ValueTask.CompletedTask;
    }

    // PDH P/Invoke
    private const int PDH_FMT_DOUBLE = 0x00000200;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    [DllImport("pdh.dll")] private static extern int PdhOpenQuery(nint dataSource, nint userData, out nint phQuery);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(nint hQuery);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(nint hQuery);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(nint hQuery, string szFullCounterPath, nint dwUserData, out nint phCounter);
    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(nint hCounter, int dwFormat, nint lpdwType, ref PDH_FMT_COUNTERVALUE pValue);
}
