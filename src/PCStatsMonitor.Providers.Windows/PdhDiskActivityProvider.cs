using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Windows;

/// <summary>
/// Disk activity via PDH "PhysicalDisk(_Total)\% Idle Time", inverted — the same
/// "Active time" figure Task Manager shows. More accurate than "% Disk Time",
/// which can exceed 100% on queued I/O.
/// Fallback when LHM exposes no "Total Activity" sensor for the drive.
/// IsThreadSafe = true: PDH query with a dedicated handle is safe for concurrent reads.
/// </summary>
public sealed class PdhDiskActivityProvider : ISensorProvider
{
    private readonly ILogger<PdhDiskActivityProvider> _log;
    private nint _query;
    private nint _counter;
    private bool _available;
    private bool _primed; // first formatted read after startup is not meaningful
    private double _activityPct = -1;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "PDH Disk Activity",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind> { MetricKind.StorageActivityPct },
    };

    public PdhDiskActivityProvider(ILogger<PdhDiskActivityProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        try
        {
            int r = PdhOpenQuery(nint.Zero, nint.Zero, out _query);
            if (r != 0) { _log.LogWarning("PDH disk: PdhOpenQuery failed 0x{Code:X}", r); return ValueTask.CompletedTask; }

            r = PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\% Idle Time", nint.Zero, out _counter);
            if (r != 0) { _log.LogWarning("PDH disk: PdhAddEnglishCounter failed 0x{Code:X}", r); return ValueTask.CompletedTask; }

            PdhCollectQueryData(_query); // prime sample (rate counters need 2 samples)
            _available = true;
            _log.LogInformation("PDH disk activity initialized");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDH disk activity provider failed to initialize");
        }
        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;
        PdhCollectQueryData(_query);

        var fmtValue = new PDH_FMT_COUNTERVALUE();
        if (PdhGetFormattedCounterValue(_counter, PDH_FMT_DOUBLE, nint.Zero, ref fmtValue) != 0) return;

        _primed = true;
        _activityPct = Math.Clamp(100.0 - fmtValue.doubleValue, 0, 100);
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available || !_primed || kind != MetricKind.StorageActivityPct) { value = 0; return false; }
        value = _activityPct;
        return _activityPct >= 0;
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;

    public ValueTask DisposeAsync()
    {
        if (_query != nint.Zero) PdhCloseQuery(_query);
        return ValueTask.CompletedTask;
    }

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
