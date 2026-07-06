using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Windows;

/// <summary>
/// CPU clock via PDH "% Processor Performance" × base frequency from the registry —
/// the same method Task Manager uses. Works without ring0/MSR access.
/// Fallback when LHM cannot read CPU clocks (e.g. its kernel driver is blocked).
/// IsThreadSafe = true: PDH query with a dedicated handle is safe for concurrent reads.
/// </summary>
public sealed class PdhCpuClockProvider : ISensorProvider
{
    private readonly ILogger<PdhCpuClockProvider> _log;
    private nint _query;
    private nint _counter;
    private double _baseMhz;
    private bool _scaleByPerformance; // true when using % Processor Performance, false for raw Processor Frequency
    private bool _available;
    private double _clockMhz;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "PDH CPU Clock",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind> { MetricKind.CpuClock },
    };

    public PdhCpuClockProvider(ILogger<PdhCpuClockProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                if (key?.GetValue("~MHz") is int mhz && mhz > 0)
                    _baseMhz = mhz;
            }

            int r = PdhOpenQuery(nint.Zero, nint.Zero, out _query);
            if (r != 0) { _log.LogWarning("PDH clock: PdhOpenQuery failed 0x{Code:X}", r); return ValueTask.CompletedTask; }

            // "% Processor Performance" can exceed 100 under turbo; multiplied by base MHz it
            // yields the effective clock. Requires the base frequency from the registry.
            if (_baseMhz > 0)
            {
                r = PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Performance", nint.Zero, out _counter);
                if (r == 0) _scaleByPerformance = true;
            }

            // Fallback: raw frequency counter (reports base/current MHz directly, no turbo detail)
            if (!_scaleByPerformance)
            {
                r = PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\Processor Frequency", nint.Zero, out _counter);
                if (r != 0) { _log.LogWarning("PDH clock: no usable counter, 0x{Code:X}", r); return ValueTask.CompletedTask; }
            }

            PdhCollectQueryData(_query); // prime sample
            _available = true;
            _log.LogInformation("PDH CPU clock initialized ({Mode}, base {Base} MHz)",
                _scaleByPerformance ? "% Processor Performance" : "Processor Frequency", _baseMhz);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDH CPU clock provider failed to initialize");
        }
        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;
        PdhCollectQueryData(_query);

        var fmtValue = new PDH_FMT_COUNTERVALUE();
        if (PdhGetFormattedCounterValue(_counter, PDH_FMT_DOUBLE, nint.Zero, ref fmtValue) != 0) return;

        _clockMhz = _scaleByPerformance
            ? _baseMhz * fmtValue.doubleValue / 100.0
            : fmtValue.doubleValue;
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available || kind != MetricKind.CpuClock) { value = 0; return false; }
        value = _clockMhz;
        return _clockMhz > 0;
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
