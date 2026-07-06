using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Windows;

/// <summary>
/// CPU temperature via PDH "Thermal Zone Information" performance counters.
/// Works on all Windows 10+ systems; no WMI/ring0 required.
/// "High Precision Temperature" counter value is in tenths of Kelvin.
/// Selects the hottest thermal zone (most likely CPU package).
/// Fallback when LHM cannot read CPU temp (e.g. Intel Arrow Lake).
/// </summary>
public sealed class PdhCpuThermalProvider : ISensorProvider
{
    private readonly ILogger<PdhCpuThermalProvider> _log;
    private nint _query;
    private nint _counter;
    private bool _available;
    private double _tempC;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "PDH CPU Thermal",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind> { MetricKind.CpuTempPackage },
    };

    public PdhCpuThermalProvider(ILogger<PdhCpuThermalProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        try
        {
            // Enumerate thermal zone instances; pick the hottest (most likely CPU)
            string? bestInstance = FindBestThermalZone();
            if (bestInstance == null)
            {
                _log.LogWarning("PDH CPU thermal: no Thermal Zone Information instances found");
                return ValueTask.CompletedTask;
            }

            int r = PdhOpenQuery(nint.Zero, nint.Zero, out _query);
            if (r != 0) { _log.LogWarning("PDH thermal: PdhOpenQuery failed 0x{Code:X}", r); return ValueTask.CompletedTask; }

            string counterPath = $@"\Thermal Zone Information({bestInstance})\High Precision Temperature";
            r = PdhAddEnglishCounter(_query, counterPath, nint.Zero, out _counter);
            if (r != 0) { _log.LogWarning("PDH thermal: PdhAddEnglishCounter failed 0x{Code:X} for {Path}", r, counterPath); return ValueTask.CompletedTask; }

            PdhCollectQueryData(_query); // prime sample
            _available = true;
            _log.LogInformation("PDH CPU thermal: using zone '{Zone}'", bestInstance);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDH CPU thermal provider failed to initialize");
        }
        return ValueTask.CompletedTask;
    }

    private static string? FindBestThermalZone()
    {
        // Discover the machine's actual thermal zone instances; zone names vary widely
        // across OEMs (\_TZ.TZ00, \_TZ.THM0, \_TZ.CPUZ, ...), so enumeration beats guessing
        var candidates = EnumerateThermalZoneInstances();

        // Static fallbacks for systems where enumeration fails
        if (candidates.Count == 0)
            candidates.AddRange([@"\_TZ.TZ00", @"\_TZ.TZ10", @"\_TZ.THRM", @"\_TZ.CPU0", @"\_TZ.CPU_"]);

        // Open a temp query to sample each zone; pick the hottest plausible one (most likely CPU)
        if (PdhOpenQuery(nint.Zero, nint.Zero, out nint q) != 0) return null;

        try
        {
            string? bestZone = null;
            double bestTemp = double.MinValue;

            foreach (var zone in candidates)
            {
                string path = $@"\Thermal Zone Information({zone})\High Precision Temperature";
                if (PdhAddEnglishCounter(q, path, nint.Zero, out nint ctr) != 0) continue;

                PdhCollectQueryData(q);
                System.Threading.Thread.Sleep(10);
                PdhCollectQueryData(q);

                var fmtValue = new PDH_FMT_COUNTERVALUE();
                if (PdhGetFormattedCounterValue(ctr, PDH_FMT_DOUBLE, nint.Zero, ref fmtValue) == 0)
                {
                    double tempC = fmtValue.doubleValue / 10.0 - 273.15;
                    // Reject zones reporting nonsense (uninitialized zones read 0 K or fixed trip points)
                    if (tempC > 1 && tempC < 120 && tempC > bestTemp)
                    {
                        bestTemp = tempC;
                        bestZone = zone;
                    }
                }
            }

            return bestZone;
        }
        finally
        {
            PdhCloseQuery(q);
        }
    }

    private static List<string> EnumerateThermalZoneInstances()
    {
        var result = new List<string>();
        try
        {
            uint counterLen = 0, instanceLen = 0;
            int r = PdhEnumObjectItems(null, null, "Thermal Zone Information",
                null, ref counterLen, null, ref instanceLen, PERF_DETAIL_WIZARD, 0);
            if (r != PDH_MORE_DATA || instanceLen == 0) return result;

            var counterBuf = new char[counterLen];
            var instanceBuf = new char[instanceLen];
            r = PdhEnumObjectItems(null, null, "Thermal Zone Information",
                counterBuf, ref counterLen, instanceBuf, ref instanceLen, PERF_DETAIL_WIZARD, 0);
            if (r != 0) return result;

            // Instance list is a null-separated multi-string terminated by a double null
            int start = 0;
            for (int i = 0; i < instanceBuf.Length; i++)
            {
                if (instanceBuf[i] != '\0') continue;
                if (i > start)
                    result.Add(new string(instanceBuf, start, i - start));
                start = i + 1;
            }
        }
        catch
        {
            // Enumeration is best-effort; caller falls back to known instance names
        }
        return result;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;
        PdhCollectQueryData(_query);
        var fmtValue = new PDH_FMT_COUNTERVALUE();
        if (PdhGetFormattedCounterValue(_counter, PDH_FMT_DOUBLE, nint.Zero, ref fmtValue) == 0)
            _tempC = fmtValue.doubleValue / 10.0 - 273.15;
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available || kind != MetricKind.CpuTempPackage) { value = 0; return false; }
        value = _tempC;
        return _tempC > 0;
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;

    public ValueTask DisposeAsync()
    {
        if (_query != nint.Zero) PdhCloseQuery(_query);
        return ValueTask.CompletedTask;
    }

    private const int PDH_FMT_DOUBLE = 0x00000200;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    private const uint PERF_DETAIL_WIZARD = 400;

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
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhEnumObjectItems(
        string? szDataSource, string? szMachineName, string szObjectName,
        char[]? mszCounterList, ref uint pcchCounterListLength,
        char[]? mszInstanceList, ref uint pcchInstanceListLength,
        uint dwDetailLevel, uint dwFlags);
    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(nint hCounter, int dwFormat, nint lpdwType, ref PDH_FMT_COUNTERVALUE pValue);
}
