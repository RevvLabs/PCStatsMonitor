using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Windows;

/// <summary>
/// NVMe/SSD temperature via IOCTL_STORAGE_QUERY_PROPERTY (StorageDeviceTemperatureProperty).
/// Works on all Windows 10+ with admin privileges; no third-party libs required.
/// STORAGE_TEMPERATURE_INFO.Temperature is already in Celsius — no conversion needed.
/// Fallback when LHM cannot read storage temperature (e.g. WD Blue SN5100 NVMe).
/// </summary>
public sealed class StorageTempProvider : ISensorProvider
{
    private readonly ILogger<StorageTempProvider> _log;
    private SafeFileHandle? _driveHandle;
    private double _tempC;
    private bool _available;

    // IOCTL_STORAGE_QUERY_PROPERTY = CTL_CODE(0x2d, 0x500, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    // = (0x2d << 16) | (0 << 14) | (0x500 << 2) | 0
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const int StorageDeviceTemperatureProperty = 0xD;

    // STORAGE_PROPERTY_QUERY: PropertyId(4) + QueryType(4) + AdditionalParameters(1)
    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType; // 0 = PropertyStandardQuery
        public byte AdditionalParameters;
    }

    // STORAGE_TEMPERATURE_DATA header (18 bytes) + variable STORAGE_TEMPERATURE_INFO entries
    // STORAGE_TEMPERATURE_INFO: Index(2)+Temperature(2)+OverThreshold(2)+UnderThreshold(2)+ValidThresholds(1)+Reserved(7) = 16 bytes
    private const int TempDataHeaderSize = 18;
    private const int TempInfoEntrySize  = 16;
    private const int TempInfoTempOffset = 2; // SHORT Temperature is at offset 2 within entry

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "IOCTL Storage Temperature",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind> { MetricKind.StorageTempC },
    };

    public StorageTempProvider(ILogger<StorageTempProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        // Try volume paths first (C: etc.), then fall back to PhysicalDrive0..3
        var candidates = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            candidates.Add($@"\\.\{drive.Name.TrimEnd('\\').TrimEnd(':')}:");
        for (int i = 0; i <= 3; i++)
            candidates.Add($@"\\.\PhysicalDrive{i}");

        foreach (string path in candidates)
        {
            try
            {
                var h = CreateFile(path,
                    0x80000000,  // GENERIC_READ
                    3,           // FILE_SHARE_READ | FILE_SHARE_WRITE
                    nint.Zero, 3, 0, nint.Zero); // OPEN_EXISTING

                if (h.IsInvalid)
                {
                    _log.LogDebug("Storage temp: {Path} open failed Win32={Error}", path, Marshal.GetLastWin32Error());
                    continue;
                }

                double? t = QueryTemperature(h, out string dbgMsg);
                _log.LogDebug("Storage temp: {Path} → {Result} ({Msg})", path, t, dbgMsg);

                if (t.HasValue && t.Value > 0)
                {
                    _driveHandle = h;
                    _tempC = t.Value;
                    _available = true;
                    _log.LogInformation("Storage temp: using {Path} → {Temp:F1}°C", path, t.Value);
                    break;
                }
                h.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Storage temp: {Path} not usable", path);
            }
        }

        if (!_available)
            _log.LogWarning("Storage temp: no drive responded to temperature query");

        return ValueTask.CompletedTask;
    }

    private double? QueryTemperature(SafeFileHandle h, out string dbg)
    {
        dbg = "";
        var query = new StoragePropertyQuery
        {
            PropertyId = StorageDeviceTemperatureProperty,
            QueryType  = 0, // PropertyStandardQuery
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
            queryBytes, (uint)queryBytes.Length,
            buf, (uint)buf.Length,
            out uint returned, nint.Zero))
        {
            dbg = $"IOCTL failed Win32={Marshal.GetLastWin32Error()}";
            return null;
        }

        dbg = $"returned={returned}";
        if (returned < TempDataHeaderSize + TempInfoEntrySize) return null;

        ushort infoCount = BitConverter.ToUInt16(buf, 6);
        dbg += $" infoCount={infoCount}";
        if (infoCount == 0) return null;

        short tempRaw = BitConverter.ToInt16(buf, TempDataHeaderSize + TempInfoTempOffset);
        dbg += $" tempRaw={tempRaw}";
        return tempRaw;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available || _driveHandle == null) return;
        double? t = QueryTemperature(_driveHandle, out _);
        if (t.HasValue && t.Value > 0) _tempC = t.Value;
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available || kind != MetricKind.StorageTempC) { value = 0; return false; }
        value = _tempC;
        return _tempC > 0;
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;

    public ValueTask DisposeAsync()
    {
        _driveHandle?.Dispose();
        return ValueTask.CompletedTask;
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
