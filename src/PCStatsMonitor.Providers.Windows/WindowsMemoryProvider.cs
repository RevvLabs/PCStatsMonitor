using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Windows;

/// <summary>
/// RAM usage via GlobalMemoryStatusEx — returns true physical memory values.
/// LHM "Memory Used" reports committed/virtual bytes (includes page file), causing
/// Used + Available to exceed physical RAM. This provider reads physical pages only.
/// No admin required.
/// </summary>
public sealed class WindowsMemoryProvider : ISensorProvider
{
    private readonly ILogger<WindowsMemoryProvider> _log;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "Windows Memory",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind>
        {
            MetricKind.RamUsedBytes,
            MetricKind.RamTotalBytes,
            MetricKind.RamLoadPct,
        },
    };

    private ulong _totalPhys;
    private ulong _availPhys;
    private uint _loadPct;
    private bool _available;

    public WindowsMemoryProvider(ILogger<WindowsMemoryProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms))
        {
            _totalPhys = ms.ullTotalPhys;
            _availPhys = ms.ullAvailPhys;
            _loadPct   = ms.dwMemoryLoad;
            _available = true;
            _log.LogInformation("Windows memory: {Total:F2} GB physical RAM detected",
                ms.ullTotalPhys / 1_073_741_824.0);
        }
        else
        {
            _log.LogWarning("Windows memory: GlobalMemoryStatusEx failed Win32={Error}",
                Marshal.GetLastWin32Error());
        }
        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms))
        {
            _totalPhys = ms.ullTotalPhys;
            _availPhys = ms.ullAvailPhys;
            _loadPct   = ms.dwMemoryLoad;
        }
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available) { value = 0; return false; }
        switch (kind)
        {
            case MetricKind.RamTotalBytes: value = _totalPhys; return _totalPhys > 0;
            case MetricKind.RamUsedBytes:  value = _totalPhys - _availPhys; return _totalPhys > 0;
            case MetricKind.RamLoadPct:    value = _loadPct; return true;
            default: value = 0; return false;
        }
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
