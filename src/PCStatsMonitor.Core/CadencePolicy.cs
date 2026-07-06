namespace PCStatsMonitor.Core;

/// <summary>
/// Maps each MetricKind to a refresh cadence (tick divisor).
/// Also maps MetricKind to its MetricGroup for provider-level Refresh calls.
/// </summary>
public sealed class CadencePolicy
{
    private static readonly IReadOnlyDictionary<MetricKind, MetricGroup> Groups =
        new Dictionary<MetricKind, MetricGroup>
        {
            // Fast — every tick
            [MetricKind.CpuLoad]         = MetricGroup.Fast,
            [MetricKind.CpuTempPackage]  = MetricGroup.Fast,
            [MetricKind.CpuPower]        = MetricGroup.Fast,
            [MetricKind.GpuLoad]         = MetricGroup.Fast,
            [MetricKind.GpuTempCore]     = MetricGroup.Fast,
            [MetricKind.GpuPower]        = MetricGroup.Fast,

            // Medium — every 2 ticks
            [MetricKind.CpuClock]        = MetricGroup.Medium,
            [MetricKind.CpuFanRpm]       = MetricGroup.Medium,
            [MetricKind.GpuClock]        = MetricGroup.Medium,
            [MetricKind.GpuFanRpm]       = MetricGroup.Medium,
            [MetricKind.GpuMemUsedBytes] = MetricGroup.Medium,

            // Slow — every 5 ticks
            [MetricKind.StorageTempC]    = MetricGroup.Slow,
            [MetricKind.RamUsedBytes]    = MetricGroup.Slow,
            [MetricKind.RamLoadPct]      = MetricGroup.Slow,
            [MetricKind.StorageUsedBytes]= MetricGroup.Slow,
            [MetricKind.StorageActivityPct] = MetricGroup.Slow,

            // Idle — every 10 ticks
            [MetricKind.RamTotalBytes]   = MetricGroup.Idle,
            [MetricKind.StorageTotalBytes]= MetricGroup.Idle,
            [MetricKind.StorageLoadPct]  = MetricGroup.Idle,
            [MetricKind.StorageLifePct]  = MetricGroup.Idle,
        };

    // Tick divisors — polling pauses entirely while the window is hidden
    // (SensorPump skips the tick), so there is only one divisor set.
    private static readonly IReadOnlyDictionary<MetricGroup, int> Divisors =
        new Dictionary<MetricGroup, int>
        {
            [MetricGroup.Fast]   = 1,
            [MetricGroup.Medium] = 2,
            [MetricGroup.Slow]   = 5,
            [MetricGroup.Idle]   = 10,
            [MetricGroup.Static] = int.MaxValue,
        };

    public MetricGroup GetGroup(MetricKind kind) =>
        Groups.TryGetValue(kind, out var g) ? g : MetricGroup.Idle;

    /// <summary>
    /// Returns the set of MetricGroups whose sensors are due for refresh on the given tick.
    /// </summary>
    public IEnumerable<MetricGroup> GroupsDueOnTick(long tick)
    {
        foreach (var (group, div) in Divisors)
        {
            if (group == MetricGroup.Static) continue;
            if (tick % div == 0)
                yield return group;
        }
    }

    public static IReadOnlySet<MetricKind> MetricsInGroup(MetricGroup group) =>
        new HashSet<MetricKind>(
            Groups.Where(kv => kv.Value == group).Select(kv => kv.Key));
}
