using LibreHardwareMonitor.Hardware;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Lhm;

/// <summary>
/// Hand-curated, exact-match lookup table from (HardwareType, SensorType, SensorName) → MetricKind.
/// No string.Contains at runtime — all matching is O(1) dictionary lookup.
///
/// Sensor names are the canonical names LHM assigns. Where multiple names map to the same
/// MetricKind, the first match in priority order wins (handled by LhmSensorProvider).
/// </summary>
public static class LhmSensorMap
{
    public sealed record SensorKey(HardwareType HwType, SensorType SensorType, string Name);

    private static readonly Dictionary<SensorKey, MetricKind> Table = new()
    {
        // ── CPU ──────────────────────────────────────────────────────────────
        // Temperature — prefer Package, fall back to hottest core / average / first core.
        // "Core Max" (hottest core, Intel hybrid/Arrow Lake) enumerates before "Core Average"
        // in LHM, so it wins by first-seen; "CPU Package" replaces either via provider override.
        { new(HardwareType.Cpu, SensorType.Temperature, "CPU Package"),       MetricKind.CpuTempPackage },
        { new(HardwareType.Cpu, SensorType.Temperature, "Core Max"),           MetricKind.CpuTempPackage },
        { new(HardwareType.Cpu, SensorType.Temperature, "Core Average"),       MetricKind.CpuTempPackage },
        { new(HardwareType.Cpu, SensorType.Temperature, "CPU Cores"),          MetricKind.CpuTempPackage },
        { new(HardwareType.Cpu, SensorType.Temperature, "Core #1"),            MetricKind.CpuTempPackage },
        // AMD Ryzen: LHM reports "Tdie" (junction temp) or "Tctl/Tdie" (offset-adjusted)
        { new(HardwareType.Cpu, SensorType.Temperature, "Tdie"),               MetricKind.CpuTempPackage },
        { new(HardwareType.Cpu, SensorType.Temperature, "Tctl/Tdie"),          MetricKind.CpuTempPackage },
        { new(HardwareType.Cpu, SensorType.Temperature, "CPU Die"),            MetricKind.CpuTempPackage },

        // Load — prefer Total
        { new(HardwareType.Cpu, SensorType.Load, "CPU Total"),                 MetricKind.CpuLoad },
        { new(HardwareType.Cpu, SensorType.Load, "Total"),                     MetricKind.CpuLoad },

        // Clock — Core #1 is the reference clock; Intel hybrid CPUs (Arrow/Meteor Lake)
        // name cores "P-Core #N" / "E-Core #N" instead
        { new(HardwareType.Cpu, SensorType.Clock, "CPU Core #1"),              MetricKind.CpuClock },
        { new(HardwareType.Cpu, SensorType.Clock, "Core #1"),                  MetricKind.CpuClock },
        { new(HardwareType.Cpu, SensorType.Clock, "P-Core #1"),                MetricKind.CpuClock },
        { new(HardwareType.Cpu, SensorType.Clock, "E-Core #1"),                MetricKind.CpuClock },

        // Power — Package is the TDP-relevant reading; AMD Ryzen uses PPT (Package Power Tracking)
        { new(HardwareType.Cpu, SensorType.Power, "CPU Package"),              MetricKind.CpuPower },
        { new(HardwareType.Cpu, SensorType.Power, "Package"),                  MetricKind.CpuPower },
        { new(HardwareType.Cpu, SensorType.Power, "PPT"),                      MetricKind.CpuPower },
        { new(HardwareType.Cpu, SensorType.Power, "CPU PPT"),                  MetricKind.CpuPower },
        // Fallbacks for CPUs where "CPU Package" reads 0 (e.g. Arrow Lake RAPL gaps) —
        // CpuPower is multi-bound; the provider picks the first non-zero reading per refresh
        { new(HardwareType.Cpu, SensorType.Power, "CPU Cores"),                MetricKind.CpuPower },
        { new(HardwareType.Cpu, SensorType.Power, "CPU Platform"),             MetricKind.CpuPower },

        // ── GPU NVIDIA ───────────────────────────────────────────────────────
        // Temperature
        { new(HardwareType.GpuNvidia, SensorType.Temperature, "GPU Core"),     MetricKind.GpuTempCore },

        // Load — D3D 3D is preferred on modern drivers (Turing+); GPU Core is fallback
        // NOTE: NVML provider takes precedence over these entries; LHM GPU load is the fallback path only
        { new(HardwareType.GpuNvidia, SensorType.Load, "D3D 3D"),             MetricKind.GpuLoad },
        { new(HardwareType.GpuNvidia, SensorType.Load, "GPU Core"),            MetricKind.GpuLoad },

        // Clock
        { new(HardwareType.GpuNvidia, SensorType.Clock, "GPU Core"),          MetricKind.GpuClock },

        // Power
        { new(HardwareType.GpuNvidia, SensorType.Power, "GPU Package"),        MetricKind.GpuPower },
        { new(HardwareType.GpuNvidia, SensorType.Power, "GPU Power"),          MetricKind.GpuPower },
        { new(HardwareType.GpuNvidia, SensorType.Power, "GPU Chip"),           MetricKind.GpuPower },

        // Fan
        { new(HardwareType.GpuNvidia, SensorType.Fan, "GPU Fan 1"),           MetricKind.GpuFanRpm },
        { new(HardwareType.GpuNvidia, SensorType.Fan, "GPU Fan"),             MetricKind.GpuFanRpm },

        // Memory (SmallData = MB; we convert to bytes)
        { new(HardwareType.GpuNvidia, SensorType.SmallData, "GPU Memory Used"), MetricKind.GpuMemUsedBytes },

        // ── GPU AMD ──────────────────────────────────────────────────────────
        { new(HardwareType.GpuAmd, SensorType.Temperature, "GPU Core"),        MetricKind.GpuTempCore },
        { new(HardwareType.GpuAmd, SensorType.Load, "GPU Core"),               MetricKind.GpuLoad },
        { new(HardwareType.GpuAmd, SensorType.Load, "D3D 3D"),                MetricKind.GpuLoad },
        { new(HardwareType.GpuAmd, SensorType.Clock, "GPU Core"),              MetricKind.GpuClock },
        { new(HardwareType.GpuAmd, SensorType.Power, "GPU Package"),           MetricKind.GpuPower },
        { new(HardwareType.GpuAmd, SensorType.Power, "GPU Total"),             MetricKind.GpuPower },
        { new(HardwareType.GpuAmd, SensorType.Fan, "GPU Fan 1"),               MetricKind.GpuFanRpm },
        { new(HardwareType.GpuAmd, SensorType.Fan, "GPU Fan"),                 MetricKind.GpuFanRpm },
        { new(HardwareType.GpuAmd, SensorType.SmallData, "GPU Memory Used"),   MetricKind.GpuMemUsedBytes },

        // ── GPU INTEL ────────────────────────────────────────────────────────
        { new(HardwareType.GpuIntel, SensorType.Temperature, "GPU Core"),      MetricKind.GpuTempCore },
        { new(HardwareType.GpuIntel, SensorType.Load, "GPU Core"),             MetricKind.GpuLoad },
        { new(HardwareType.GpuIntel, SensorType.Load, "D3D 3D"),              MetricKind.GpuLoad },
        { new(HardwareType.GpuIntel, SensorType.Clock, "GPU Core"),            MetricKind.GpuClock },
        { new(HardwareType.GpuIntel, SensorType.Power, "GPU Package"),         MetricKind.GpuPower },
        { new(HardwareType.GpuIntel, SensorType.Fan, "GPU Fan"),               MetricKind.GpuFanRpm },

        // ── MEMORY ───────────────────────────────────────────────────────────
        // Data sensors are in GB (2^30 bytes); we store as bytes
        { new(HardwareType.Memory, SensorType.Data, "Memory Used"),            MetricKind.RamUsedBytes },
        { new(HardwareType.Memory, SensorType.Data, "Memory Available"),       MetricKind.RamTotalBytes }, // stored as available; provider computes total
        { new(HardwareType.Memory, SensorType.Load, "Memory"),                 MetricKind.RamLoadPct },

        // ── STORAGE ──────────────────────────────────────────────────────────
        // Temperature — exact matches only, no Warning/Critical thresholds
        { new(HardwareType.Storage, SensorType.Temperature, "Temperature"),    MetricKind.StorageTempC },
        { new(HardwareType.Storage, SensorType.Temperature, "Temperature 1"),  MetricKind.StorageTempC },
        { new(HardwareType.Storage, SensorType.Temperature, "Composite"),      MetricKind.StorageTempC },

        // Health — NVMe/SMART remaining-life percentage and drive activity
        { new(HardwareType.Storage, SensorType.Level, "Life"),                 MetricKind.StorageLifePct },
        { new(HardwareType.Storage, SensorType.Level, "Remaining Life"),       MetricKind.StorageLifePct },
        { new(HardwareType.Storage, SensorType.Load, "Total Activity"),        MetricKind.StorageActivityPct },

        // ── SuperIO / Motherboard FAN sensors (for CPU fan) ─────────────────
        { new(HardwareType.SuperIO, SensorType.Fan, "CPU Fan"),                MetricKind.CpuFanRpm },
        { new(HardwareType.SuperIO, SensorType.Fan, "CPU FAN"),                MetricKind.CpuFanRpm },
        { new(HardwareType.SuperIO, SensorType.Fan, "CPU_FAN"),                MetricKind.CpuFanRpm },
        { new(HardwareType.Motherboard, SensorType.Fan, "CPU Fan"),            MetricKind.CpuFanRpm },
        { new(HardwareType.Motherboard, SensorType.Fan, "CPU FAN"),            MetricKind.CpuFanRpm },
        
        // Generic fallbacks for motherboards that don't label the CPU fan specifically (like ITE IT8696E)
        { new(HardwareType.SuperIO, SensorType.Fan, "Fan #1"),                 MetricKind.CpuFanRpm },
        { new(HardwareType.SuperIO, SensorType.Fan, "Fan #2"),                 MetricKind.CpuFanRpm },
        { new(HardwareType.SuperIO, SensorType.Fan, "Fan #3"),                 MetricKind.CpuFanRpm },
        { new(HardwareType.SuperIO, SensorType.Fan, "Fan #4"),                 MetricKind.CpuFanRpm },
        { new(HardwareType.SuperIO, SensorType.Fan, "Fan #5"),                 MetricKind.CpuFanRpm },
        { new(HardwareType.SuperIO, SensorType.Fan, "Fan #6"),                 MetricKind.CpuFanRpm },
        { new(HardwareType.SuperIO, SensorType.Fan, "Fan 1"),                  MetricKind.CpuFanRpm },
        { new(HardwareType.SuperIO, SensorType.Fan, "Fan1"),                   MetricKind.CpuFanRpm },
        { new(HardwareType.SuperIO, SensorType.Fan, "CPU Fan 1"),              MetricKind.CpuFanRpm },
        { new(HardwareType.Motherboard, SensorType.Fan, "Fan #1"),             MetricKind.CpuFanRpm },
        { new(HardwareType.Motherboard, SensorType.Fan, "Fan 1"),              MetricKind.CpuFanRpm },
        { new(HardwareType.Motherboard, SensorType.Fan, "Fan1"),               MetricKind.CpuFanRpm },
        { new(HardwareType.Motherboard, SensorType.Fan, "CPU Fan 1"),          MetricKind.CpuFanRpm },
    };

    // Priority: sensors listed earlier in Table take precedence.
    // For a given hardware type + metric kind, we track the first-seen sensor.
    public static bool TryGetMetric(HardwareType hwType, SensorType sensorType, string name, out MetricKind kind)
        => Table.TryGetValue(new SensorKey(hwType, sensorType, name), out kind);

    // GPU hardware types for fast lookup
    public static readonly IReadOnlySet<HardwareType> GpuTypes =
        new HashSet<HardwareType> { HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel };
}
