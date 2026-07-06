namespace PCStatsMonitor.Core;

public sealed class ProviderCapabilities
{
    public required string Name { get; init; }

    /// <summary>
    /// If true, Refresh() may be called concurrently from Parallel.ForEach inside the pump thread.
    /// Providers backed by file/sysfs reads are safe; LHM is not.
    /// </summary>
    public bool IsThreadSafe { get; init; }

    /// <summary>The set of MetricKinds this provider can supply.</summary>
    public required IReadOnlySet<MetricKind> SupportedMetrics { get; init; }
}
