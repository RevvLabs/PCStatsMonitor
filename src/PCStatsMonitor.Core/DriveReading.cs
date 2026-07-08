namespace PCStatsMonitor.Core;

/// <summary>
/// Point-in-time readings for a single physical drive. Immutable — safe to share
/// across threads inside a SensorSnapshot. One of these exists per physical disk
/// the machine reports (multiple SSDs/HDDs each get their own).
/// </summary>
/// <param name="Index">Physical disk number (matches \\.\PhysicalDriveN).</param>
/// <param name="Model">Friendly model name, e.g. "Samsung SSD 990 PRO 1TB".</param>
/// <param name="BusType">"NVMe", "SATA", "USB", "RAID", or "" when unknown.</param>
/// <param name="MediaType">"SSD", "HDD", or "" when unknown.</param>
/// <param name="Volumes">Space-separated drive letters, e.g. "C: D:" (empty when unmounted).</param>
/// <param name="UsedBytes">Bytes used across this drive's mounted volumes.</param>
/// <param name="TotalBytes">Total capacity across this drive's mounted volumes.</param>
/// <param name="LoadPct">Used space as a percentage (0 when TotalBytes is 0).</param>
/// <param name="TempC">Temperature in °C; 0 = no sensor / not available.</param>
/// <param name="LifePct">Remaining life as a percentage; -1 = not available.</param>
public sealed record DriveReading(
    int Index,
    string Model,
    string BusType,
    string MediaType,
    string Volumes,
    double UsedBytes,
    double TotalBytes,
    double LoadPct,
    double TempC,
    double LifePct)
{
    /// <summary>"NVMe SSD", "SATA HDD", or whichever parts are known.</summary>
    public string TypeLabel =>
        string.Join(' ', new[] { BusType, MediaType }.Where(s => !string.IsNullOrEmpty(s)));
}
