using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Windows;

/// <summary>
/// CPU package power via Windows Energy Metering Interface (EMI).
/// Available on Windows 10 1809+ with supported hardware.
/// Computes average power as ΔEnergy / ΔTime between consecutive calls.
/// Fallback when LHM cannot read CPU power (e.g. Intel Arrow Lake).
/// </summary>
public sealed class EmiCpuPowerProvider : ISensorProvider
{
    private readonly ILogger<EmiCpuPowerProvider> _log;
    private SafeFileHandle? _handle;
    private ulong _lastEnergy;
    private ulong _lastTime;
    private double _powerW;
    private bool _available;

    // IOCTL_EMI_GET_MEASUREMENT = CTL_CODE(FILE_DEVICE_EMI=0x9a, 2, METHOD_BUFFERED, FILE_READ_ACCESS=1)
    // = (0x9a << 16) | (1 << 14) | (2 << 2) | 0 = 0x009a4008
    private const uint IOCTL_EMI_GET_MEASUREMENT = 0x009a4008;

    // AbsoluteEnergy: pico-watt-hours; RelativeTime: 100-nanosecond units
    // Power(W) = ΔEnergy(pWh) × 3.6e-9 J/pWh ÷ (ΔTime × 1e-7 s/unit)
    //          = ΔEnergy × 36000 ÷ ΔTime
    private const double EnergyToWatts = 36000.0;

    [StructLayout(LayoutKind.Sequential)]
    private struct EmiMeasurement
    {
        public ulong AbsoluteEnergy; // pico-watt-hours
        public ulong RelativeTime;   // 100-ns units
    }

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "EMI CPU Power",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind> { MetricKind.CpuPower },
    };

    public EmiCpuPowerProvider(ILogger<EmiCpuPowerProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        // Try known EMI device paths (index 0..3 covers most systems)
        for (int i = 0; i <= 3; i++)
        {
            string path = $@"\\.\EnergyMeter{i}";
            try
            {
                var handle = CreateFile(path,
                    0x80000000, // GENERIC_READ
                    3,          // FILE_SHARE_READ | FILE_SHARE_WRITE
                    nint.Zero, 3, 0, nint.Zero); // OPEN_EXISTING

                if (handle.IsInvalid) continue;

                // Prime first measurement
                var m = ReadMeasurement(handle);
                if (m.HasValue)
                {
                    _handle = handle;
                    _lastEnergy = m.Value.AbsoluteEnergy;
                    _lastTime   = m.Value.RelativeTime;
                    _available  = true;
                    _log.LogInformation("EMI CPU power: opened {Path}", path);
                    break;
                }
                handle.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "EMI device {Path} not available", path);
            }
        }

        if (!_available)
            _log.LogWarning("EMI CPU power: no Energy Meter device found (Windows 10 1809+ required)");

        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available || _handle == null) return;
        var m = ReadMeasurement(_handle);
        if (!m.HasValue) return;

        ulong dE = m.Value.AbsoluteEnergy - _lastEnergy;
        ulong dT = m.Value.RelativeTime   - _lastTime;

        if (dT > 0)
            _powerW = dE * EnergyToWatts / dT;

        _lastEnergy = m.Value.AbsoluteEnergy;
        _lastTime   = m.Value.RelativeTime;
    }

    private EmiMeasurement? ReadMeasurement(SafeFileHandle handle)
    {
        var buf = new byte[Marshal.SizeOf<EmiMeasurement>()];
        if (!DeviceIoControl(handle, IOCTL_EMI_GET_MEASUREMENT,
            nint.Zero, 0, buf, (uint)buf.Length, out _, nint.Zero))
            return null;

        var m = new EmiMeasurement();
        m.AbsoluteEnergy = BitConverter.ToUInt64(buf, 0);
        m.RelativeTime   = BitConverter.ToUInt64(buf, 8);
        return m;
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available || kind != MetricKind.CpuPower) { value = 0; return false; }
        value = _powerW;
        return _powerW > 0;
    }

    public HardwareIdentity? ResolveIdentity(string domain) => null;

    public ValueTask DisposeAsync()
    {
        _handle?.Dispose();
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
        nint lpInBuffer, uint nInBufferSize,
        byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, nint lpOverlapped);
}
