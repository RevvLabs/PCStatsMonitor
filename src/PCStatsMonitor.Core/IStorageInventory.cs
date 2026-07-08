using System.Collections.Immutable;

namespace PCStatsMonitor.Core;

/// <summary>
/// Optional capability implemented by providers that can enumerate every physical
/// drive (not just the system drive). SensorPump folds the returned list into each
/// SensorSnapshot's <see cref="SensorSnapshot.Drives"/>. The first provider that
/// returns a non-empty list wins, mirroring the priority-order rule for flat metrics.
/// </summary>
public interface IStorageInventory
{
    ImmutableArray<DriveReading> ReadDrives();
}
