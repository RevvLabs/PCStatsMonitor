using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Lhm;

/// <summary>
/// Wraps LibreHardwareMonitor's Computer class. Must run on a single thread — LHM is not thread-safe.
///
/// Key design decisions:
/// - hardware.Update() is called per hardware type only when their cadence group is due.
/// - Sensor readings are cached in a local Dictionary after each Update; TryRead is lock-free.
/// - Sensor → MetricKind binding is built once at startup from LhmSensorMap (no runtime string.Contains).
/// - RAM "Available" is stored separately to compute total = used + available.
/// </summary>
public sealed class LhmSensorProvider : ISensorProvider
{
    private readonly ILogger<LhmSensorProvider> _log;
    private Computer? _computer;

    // Cached readings — written only from the pump thread, read from TryRead (also pump thread).
    private readonly Dictionary<MetricKind, double> _cache = new();

    // Sensor bindings resolved at startup
    private readonly List<(IHardware Hardware, ISensor Sensor, MetricKind Kind, bool IsRamAvailable)> _bindings = new();

    // Hardware partitioned by cadence group for selective Update() calls
    private readonly Dictionary<MetricGroup, List<IHardware>> _hardwareByGroup = new();

    // Available RAM (GB) stored separately so total = used + available
    private double _ramAvailableGb;

    // Kinds where several sensors are bound in priority order and the first
    // non-zero reading wins per refresh (e.g. Arrow Lake "CPU Package" power reads 0;
    // "CPU Cores" / "CPU Platform" act as live fallbacks).
    private static readonly HashSet<MetricKind> MultiBindKinds = new() { MetricKind.CpuPower };

    // Identity strings
    private string _cpuName = "Unknown CPU";
    private string _gpuName = "Unknown GPU";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "LibreHardwareMonitor",
        IsThreadSafe = false,
        SupportedMetrics = new HashSet<MetricKind>((MetricKind[])Enum.GetValues(typeof(MetricKind)))
    };

    public LhmSensorProvider(ILogger<LhmSensorProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true,
        };
        _computer.Open();

        // Do one full update to discover sensors
        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware)
                sub.Update();
        }

        BuildBindings();
        return ValueTask.CompletedTask;
    }

    private void BuildBindings()
    {
        if (_computer == null) return;

        // Initialise group buckets
        foreach (MetricGroup g in Enum.GetValues(typeof(MetricGroup)))
            _hardwareByGroup[g] = new List<IHardware>();

        // First-seen wins per MetricKind — matches priority order in LhmSensorMap
        var seen = new HashSet<MetricKind>();

        void ProcessHardware(IHardware hw, MetricGroup group)
        {
            bool addedToGroup = false;

            foreach (var sensor in hw.Sensors)
            {
                if (!LhmSensorMap.TryGetMetric(hw.HardwareType, sensor.SensorType, sensor.Name, out var kind))
                {
                    _log.LogDebug("Skipped: {HwType}/{HwName} | {SensorType} | \"{SensorName}\"", hw.HardwareType, hw.Name, sensor.SensorType, sensor.Name);
                    continue;
                }

                // Explicit override:
                // 1. If we already bound GpuLoad, but this sensor is D3D 3D (which accurately reflects Windows Task Manager load), replace it!
                // 2. If we already bound CpuFanRpm to a generic Fan #, but this sensor is explicitly named CPU, replace it!
                // 3. If we already bound CpuTempPackage (e.g. Core Max), but this sensor is the canonical "CPU Package", replace it!
                if (seen.Contains(kind))
                {
                    // Only override a binding already owned by THIS hardware — on multi-GPU
                    // systems (discrete + integrated) "seen" is global, so without this check
                    // whichever GPU happens to enumerate last would steal GpuLoad from the
                    // GPU that's actually meant to win (see BuildHardwareOrder).
                    bool sameHwBound = _bindings.Any(b => b.Hardware == hw && b.Kind == kind);

                    if (sameHwBound && kind == MetricKind.GpuLoad && sensor.Name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase))
                    {
                        _bindings.RemoveAll(b => b.Hardware == hw && b.Kind == MetricKind.GpuLoad);
                        _log.LogDebug("Overriding GpuLoad binding with higher-priority D3D 3D sensor");
                    }
                    else if (sameHwBound && kind == MetricKind.CpuFanRpm && sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                    {
                        _bindings.RemoveAll(b => b.Hardware == hw && b.Kind == MetricKind.CpuFanRpm);
                        _log.LogDebug("Overriding CpuFanRpm generic binding with explicit CPU fan sensor");
                    }
                    else if (sameHwBound && kind == MetricKind.CpuTempPackage && sensor.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
                    {
                        _bindings.RemoveAll(b => b.Hardware == hw && b.Kind == MetricKind.CpuTempPackage);
                        _log.LogDebug("Overriding CpuTempPackage binding with canonical CPU Package sensor");
                    }
                    else
                    {
                        continue;
                    }
                }

                bool isRamAvailable = kind == MetricKind.RamTotalBytes;
                _bindings.Add((hw, sensor, kind, isRamAvailable));

                // Multi-bind kinds keep collecting sensors in enumeration order;
                // Refresh picks the first non-zero reading.
                if (!isRamAvailable && !MultiBindKinds.Contains(kind))
                    seen.Add(kind);

                if (!addedToGroup)
                {
                    _hardwareByGroup[group].Add(hw);
                    addedToGroup = true;
                }

                _log.LogDebug("Bound: {HwName}/{SensorName} → {Kind}", hw.Name, sensor.Name, kind);
            }
        }

        // Process integrated Intel GPU last so a discrete GPU (Nvidia/Amd), if present,
        // always wins the first-seen GpuLoad/GpuTempCore/etc. bindings regardless of
        // whatever order LHM happens to enumerate adapters in on a given machine.
        var orderedHardware = _computer.Hardware.OrderBy(hw => hw.HardwareType == HardwareType.GpuIntel ? 1 : 0);

        foreach (var hw in orderedHardware)
        {
            MetricGroup group = hw.HardwareType switch
            {
                HardwareType.Cpu                                              => MetricGroup.Fast,
                HardwareType.GpuNvidia or HardwareType.GpuAmd
                    or HardwareType.GpuIntel                                  => MetricGroup.Fast,
                HardwareType.Memory                                           => MetricGroup.Slow,
                HardwareType.Storage                                          => MetricGroup.Slow,
                HardwareType.Motherboard or HardwareType.SuperIO             => MetricGroup.Medium,
                _                                                             => MetricGroup.Idle,
            };

            // Capture names
            if (hw.HardwareType == HardwareType.Cpu && _cpuName == "Unknown CPU")
                _cpuName = hw.Name;
            if (LhmSensorMap.GpuTypes.Contains(hw.HardwareType) && _gpuName == "Unknown GPU")
                _gpuName = hw.Name;

            ProcessHardware(hw, group);

            foreach (var sub in hw.SubHardware)
                ProcessHardware(sub, group);
        }

        _log.LogInformation("LHM: {Count} sensor bindings established", _bindings.Count);
    }

    public void Refresh(MetricGroup group)
    {
        if (_computer == null) return;

        // Update only hardware whose cadence group matches
        if (!_hardwareByGroup.TryGetValue(group, out var hardwareList)) return;

        foreach (var hw in hardwareList)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware)
                sub.Update();
        }

        // Read bound sensors into cache.
        // For multi-bind kinds the first non-zero reading (in binding/priority order) wins.
        var multiBindWritten = new HashSet<MetricKind>();
        foreach (var (_, sensor, kind, isRamAvailable) in _bindings)
        {
            if (!sensor.Value.HasValue) continue;
            double val = sensor.Value.Value;

            if (isRamAvailable)
            {
                // "Memory Available" is stored in GB; remember it to compute total
                _ramAvailableGb = val;
                continue;
            }

            if (MultiBindKinds.Contains(kind))
            {
                if (val > 0 && multiBindWritten.Add(kind))
                    _cache[kind] = val;
                continue;
            }

            // Convert units where needed
            val = kind switch
            {
                // LHM Data sensors are in GB (2^30); convert to bytes for uniformity
                MetricKind.RamUsedBytes     => val * 1_073_741_824.0,
                // VRAM SmallData is in MB; convert to bytes
                MetricKind.GpuMemUsedBytes  => val * 1_048_576.0,
                _ => val,
            };

            _cache[kind] = val;
        }

        // Compute RamTotalBytes = used + available (both in GB from LHM, so convert together)
        if (_ramAvailableGb > 0 && _cache.TryGetValue(MetricKind.RamUsedBytes, out double usedBytes))
        {
            double totalBytes = usedBytes + _ramAvailableGb * 1_073_741_824.0;
            _cache[MetricKind.RamTotalBytes] = totalBytes;
        }
    }

    public bool TryRead(MetricKind kind, out double value) => _cache.TryGetValue(kind, out value);

    public HardwareIdentity? ResolveIdentity(string domain) => domain switch
    {
        "CPU"     => new HardwareIdentity("CPU",     "CPU",     _cpuName),
        "GPU"     => new HardwareIdentity("GPU",     "GPU",     _gpuName),
        "Memory"  => new HardwareIdentity("Memory",  "System",  "System Memory"),
        "Storage" => new HardwareIdentity("Storage", "Storage", "NVMe Drive"),
        _         => null,
    };

    public ValueTask DisposeAsync()
    {
        try { _computer?.Close(); } catch { /* LHM may throw on close */ }
        return ValueTask.CompletedTask;
    }
}
