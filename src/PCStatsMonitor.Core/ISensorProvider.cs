namespace PCStatsMonitor.Core;

/// <summary>
/// Abstraction for a hardware sensor data source.
/// All implementations must be safe to call Refresh/TryRead from a single dedicated thread
/// (the SensorPump thread). Thread-safe providers may additionally be called concurrently.
/// </summary>
public interface ISensorProvider : IAsyncDisposable
{
    ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Called once before the pump loop starts. May throw; caller catches and disables provider.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken ct);

    /// <summary>
    /// Refresh cached sensor readings for the given group. Called by SensorPump on each tick
    /// where CadencePolicy determines this group is due.
    /// </summary>
    void Refresh(MetricGroup group);

    /// <summary>
    /// Read the last-refreshed value for a metric. Returns false if this provider has no data.
    /// All reads are non-blocking — values were populated during the last Refresh call.
    /// </summary>
    bool TryRead(MetricKind kind, out double value);

    /// <summary>
    /// Resolve hardware identity for a domain ("CPU", "GPU", "Memory", "Storage").
    /// Called once at startup with MetricGroup.Static.
    /// </summary>
    HardwareIdentity? ResolveIdentity(string domain);
}
