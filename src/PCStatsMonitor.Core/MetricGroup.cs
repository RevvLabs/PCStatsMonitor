namespace PCStatsMonitor.Core;

/// <summary>
/// Refresh cadence bucket. Each group is polled at a different interval multiple of the 1s base tick.
/// </summary>
public enum MetricGroup
{
    /// <summary>Every tick (1s visible, 5s hidden).</summary>
    Fast,
    /// <summary>Every 2 ticks (2s visible, 10s hidden).</summary>
    Medium,
    /// <summary>Every 5 ticks (5s visible, 30s hidden).</summary>
    Slow,
    /// <summary>Every 10 ticks (10s visible, 60s hidden).</summary>
    Idle,
    /// <summary>Once at startup only.</summary>
    Static,
}
