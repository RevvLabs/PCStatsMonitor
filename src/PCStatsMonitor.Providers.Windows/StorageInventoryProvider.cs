using System.Collections.Immutable;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Windows;

/// <summary>
/// Enumerates every physical drive via WMI (root\Microsoft\Windows\Storage) so the UI
/// can show one card per SSD/HDD instead of just the system drive. Reports model, bus
/// (NVMe/SATA/USB), media (SSD/HDD), mounted volume letters, used/total space, temperature
/// and remaining life. Provides no flat MetricKind — it feeds SensorSnapshot.Drives via
/// <see cref="IStorageInventory"/>.
///
/// IsThreadSafe = true (each WMI searcher is independent), but WMI storage queries are heavy,
/// so an internal 4s throttle keeps the cost off the 1s pump tick.
/// </summary>
public sealed class StorageInventoryProvider : ISensorProvider, IStorageInventory
{
    private readonly ILogger<StorageInventoryProvider> _log;
    private ManagementScope? _scope;
    private bool _available;

    private ImmutableArray<DriveReading> _drives = ImmutableArray<DriveReading>.Empty;
    private readonly object _drivesGate = new();
    private readonly Stopwatch _sinceLastQuery = Stopwatch.StartNew();
    private bool _firstDone;
    private static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(4);

    private static readonly ConnectionOptions WmiOptions = new()
    {
        Impersonation = ImpersonationLevel.Impersonate,
        Authentication = AuthenticationLevel.PacketPrivacy,
    };

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "Storage Inventory (WMI)",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind>(), // inventory only — no flat metrics
    };

    public StorageInventoryProvider(ILogger<StorageInventoryProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage", WmiOptions);
            scope.Connect();
            _scope = scope;
            _available = true;
            QueryDrives(); // prime so the first snapshot already has drives
            _log.LogInformation("Storage inventory: found {Count} physical drive(s)", _drives.Length);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Storage inventory provider failed to initialize — falling back to single-drive view");
        }
        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;
        if (_firstDone && _sinceLastQuery.Elapsed < QueryInterval) return;
        try { QueryDrives(); }
        catch (Exception ex) { _log.LogDebug(ex, "Storage inventory refresh failed"); }
    }

    private void QueryDrives()
    {
        _sinceLastQuery.Restart();
        _firstDone = true;
        if (_scope == null) return;

        // DiskNumber -> mounted drive letters ("C:", "D:")
        var lettersByDisk = new Dictionary<int, List<string>>();
        using (var parts = new ManagementObjectSearcher(_scope,
                   new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition")))
        {
            foreach (ManagementBaseObject p in parts.Get())
            {
                using (p)
                {
                    int diskNum = ToInt(p["DiskNumber"]);
                    object? dl = p["DriveLetter"];
                    if (dl == null) continue;
                    char c = Convert.ToChar(dl);
                    if (!char.IsLetter(c)) continue;
                    if (!lettersByDisk.TryGetValue(diskNum, out var list))
                        lettersByDisk[diskNum] = list = new List<string>();
                    list.Add($"{char.ToUpperInvariant(c)}:");
                }
            }
        }

        var builder = ImmutableArray.CreateBuilder<DriveReading>();
        using (var disks = new ManagementObjectSearcher(_scope,
                   new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk")))
        {
            foreach (ManagementBaseObject obj in disks.Get())
            {
                var disk = (ManagementObject)obj;
                using (disk)
                {
                    int index = ToInt(disk["DeviceId"]);
                    string model = (disk["FriendlyName"]?.ToString() ?? "Drive").Trim();
                    string bus = BusName(ToInt(disk["BusType"]));
                    string media = MediaName(ToInt(disk["MediaType"]), bus);

                    lettersByDisk.TryGetValue(index, out var letters);
                    letters ??= new List<string>();
                    string volumes = string.Join(' ', letters.OrderBy(s => s));

                    // Space from mounted volumes (cheap, no WMI)
                    double used = 0, total = 0;
                    foreach (string letter in letters)
                    {
                        try
                        {
                            var di = new DriveInfo(letter);
                            if (!di.IsReady) continue;
                            total += di.TotalSize;
                            used += di.TotalSize - di.AvailableFreeSpace;
                        }
                        catch { /* transient volume — skip */ }
                    }
                    // No mounted volume — fall back to raw physical capacity so the card still shows size
                    if (total == 0) total = ToDouble(disk["Size"]);
                    double loadPct = total > 0 ? used / total * 100.0 : 0;

                    // Temperature + wear from the drive's reliability counter (needs elevation; best-effort)
                    double tempC = 0, lifePct = -1;
                    try
                    {
                        foreach (ManagementBaseObject rcObj in disk.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            using (rcObj)
                            {
                                double t = ToDouble(rcObj["Temperature"]);
                                if (t > 0 && t < 200) tempC = t;
                                object? wear = rcObj["Wear"];
                                if (wear != null)
                                {
                                    double w = ToDouble(wear);
                                    if (w >= 0 && w <= 100) lifePct = 100.0 - w;
                                }
                            }
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogTrace(ex, "Reliability counter unavailable for disk {Index}", index);
                    }

                    // Reliability counter is often blocked/unsupported — fall back to the IOCTL
                    // temperature query (same one StorageTempProvider uses) so cards still show °C.
                    if (tempC <= 0)
                        tempC = TryIoctlTempC(index) ?? 0;

                    builder.Add(new DriveReading(
                        index, model, bus, media, volumes,
                        used, total, loadPct, tempC, lifePct));
                }
            }
        }

        builder.Sort((a, b) => a.Index.CompareTo(b.Index));
        var result = builder.ToImmutable();
        lock (_drivesGate) _drives = result;
    }

    public ImmutableArray<DriveReading> ReadDrives()
    {
        lock (_drivesGate) return _drives;
    }

    public bool TryRead(MetricKind kind, out double value) { value = 0; return false; }
    public HardwareIdentity? ResolveIdentity(string domain) => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static int ToInt(object? o)
    {
        if (o == null) return -1;
        return o is string s ? (int.TryParse(s, out int v) ? v : -1) : Convert.ToInt32(o);
    }

    private static double ToDouble(object? o) => o == null ? 0 : Convert.ToDouble(o);

    // MSFT_PhysicalDisk.BusType (subset that matters for consumer drives)
    private static string BusName(int bus) => bus switch
    {
        17 => "NVMe",
        11 => "SATA",
        3  => "ATA",
        7  => "USB",
        8  => "RAID",
        10 => "SAS",
        12 => "SD",
        13 => "MMC",
        _  => "",
    };

    // MSFT_PhysicalDisk.MediaType: 3=HDD, 4=SSD, 5=SCM; NVMe implies SSD when unspecified
    private static string MediaName(int media, string bus) => media switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SSD",
        _ => bus == "NVMe" ? "SSD" : "",
    };

    // --- IOCTL temperature fallback (per \\.\PhysicalDriveN) ---------------------------
    // Mirrors StorageTempProvider: IOCTL_STORAGE_QUERY_PROPERTY / StorageDeviceTemperatureProperty.
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const int StorageDeviceTemperatureProperty = 0xD;
    private const int TempDataHeaderSize = 18;
    private const int TempInfoEntrySize = 16;
    private const int TempInfoTempOffset = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    private double? TryIoctlTempC(int index)
    {
        try
        {
            using var h = CreateFile($@"\\.\PhysicalDrive{index}",
                0x80000000, 3, nint.Zero, 3, 0, nint.Zero);
            if (h.IsInvalid) return null;

            var query = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceTemperatureProperty,
                QueryType = 0,
            };
            int querySize = Marshal.SizeOf<StoragePropertyQuery>();
            byte[] queryBytes = new byte[querySize];
            var ptr = Marshal.AllocHGlobal(querySize);
            try
            {
                Marshal.StructureToPtr(query, ptr, false);
                Marshal.Copy(ptr, queryBytes, 0, querySize);
            }
            finally { Marshal.FreeHGlobal(ptr); }

            byte[] buf = new byte[TempDataHeaderSize + 4 * TempInfoEntrySize];
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY,
                    queryBytes, (uint)queryBytes.Length, buf, (uint)buf.Length,
                    out uint returned, nint.Zero))
                return null;

            if (returned < TempDataHeaderSize + TempInfoEntrySize) return null;
            ushort infoCount = BitConverter.ToUInt16(buf, 6);
            if (infoCount == 0) return null;
            short tempRaw = BitConverter.ToInt16(buf, TempDataHeaderSize + TempInfoTempOffset);
            return tempRaw > 0 && tempRaw < 200 ? tempRaw : null;
        }
        catch (Exception ex)
        {
            _log.LogTrace(ex, "IOCTL temp fallback failed for disk {Index}", index);
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize,
        byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, nint lpOverlapped);
}
