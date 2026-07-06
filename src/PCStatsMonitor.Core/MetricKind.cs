namespace PCStatsMonitor.Core;

public enum MetricKind
{
    CpuLoad,
    CpuTempPackage,
    CpuPower,
    CpuClock,
    CpuFanRpm,

    GpuLoad,
    GpuTempCore,
    GpuPower,
    GpuClock,
    GpuFanRpm,
    GpuMemUsedBytes,

    RamUsedBytes,
    RamTotalBytes,
    RamLoadPct,

    StorageTempC,
    StorageUsedBytes,
    StorageTotalBytes,
    StorageLoadPct,
    StorageLifePct,
    StorageActivityPct,
}
