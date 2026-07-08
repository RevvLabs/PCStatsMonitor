using System.Collections.Immutable;

namespace PCStatsMonitor.Core;

/// <summary>
/// Immutable point-in-time snapshot of all sensor readings.
/// Published by SensorPump via Volatile.Write; read by UI via Volatile.Read.
/// Never mutated after construction — safe to read from any thread.
/// </summary>
public sealed record SensorSnapshot(
    long SeqNo,
    long TimestampTicks,
    HardwareIdentity Cpu,
    HardwareIdentity Gpu,
    HardwareIdentity Memory,
    HardwareIdentity Storage,
    ImmutableDictionary<MetricKind, double> Metrics,
    ImmutableArray<DriveReading> Drives)
{
    public static readonly SensorSnapshot Empty = new(
        0, 0,
        HardwareIdentity.Unknown,
        HardwareIdentity.Unknown,
        HardwareIdentity.Unknown,
        HardwareIdentity.Unknown,
        ImmutableDictionary<MetricKind, double>.Empty,
        ImmutableArray<DriveReading>.Empty);

    public double Get(MetricKind kind, double fallback = 0.0) =>
        Metrics.TryGetValue(kind, out var v) ? v : fallback;

    /// <summary>True when a provider actually produced this metric — distinguishes
    /// a genuine zero reading (e.g. a stopped semi-passive fan) from "no sensor".</summary>
    public bool Has(MetricKind kind) => Metrics.ContainsKey(kind);
}
