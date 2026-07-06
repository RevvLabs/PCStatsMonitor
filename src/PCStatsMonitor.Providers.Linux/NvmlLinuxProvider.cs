using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Linux;

/// <summary>
/// NVIDIA GPU via NVML on Linux (libnvidia-ml.so.1).
/// Same NVML 11 baseline as the Windows version — resolves at runtime via dlopen.
/// IsThreadSafe = true: NVML is safe for concurrent calls after nvmlInit.
/// </summary>
public sealed class NvmlLinuxProvider : ISensorProvider
{
    private readonly ILogger<NvmlLinuxProvider> _log;
    private nint _lib, _device;
    private bool _available;
    private string _gpuName = "NVIDIA GPU";

    private double _gpuLoad, _gpuTemp, _gpuPower, _gpuClock, _gpuFan, _gpuMemUsed;

    // Delegate types (must match Windows version signatures exactly for NVML ABI)
    private delegate int NvmlInit();
    private delegate int NvmlShutdown();
    private delegate int NvmlDeviceGetHandleByIndex(uint idx, out nint handle);
    private delegate int NvmlDeviceGetUtilizationRates(nint handle, out NvmlUtil util);
    private delegate int NvmlDeviceGetTemperature(nint handle, out uint temp);
    private delegate int NvmlDeviceGetClockInfo(nint handle, uint type, out uint mhz);
    private delegate int NvmlDeviceGetPowerUsage(nint handle, out uint mw);
    private delegate int NvmlDeviceGetFanSpeedEx(nint handle, uint fanIdx, out uint speed);
    private delegate int NvmlDeviceGetMemoryInfo(nint handle, out NvmlMem mem);
    private delegate int NvmlDeviceGetName(nint handle, byte[] buf, uint len);

    private NvmlInit? _init;
    private NvmlShutdown? _shutdown;
    private NvmlDeviceGetHandleByIndex? _getHandle;
    private NvmlDeviceGetUtilizationRates? _getUtil;
    private NvmlDeviceGetTemperature? _getTemp;
    private NvmlDeviceGetClockInfo? _getClock;
    private NvmlDeviceGetPowerUsage? _getPower;
    private NvmlDeviceGetFanSpeedEx? _getFan;
    private NvmlDeviceGetMemoryInfo? _getMem;
    private NvmlDeviceGetName? _getName;

    [StructLayout(LayoutKind.Sequential)] private struct NvmlUtil { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct NvmlMem  { public ulong Total, Free, Used; }

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "NVML Linux (NVIDIA)",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind>
        {
            MetricKind.GpuLoad, MetricKind.GpuTempCore, MetricKind.GpuPower,
            MetricKind.GpuClock, MetricKind.GpuFanRpm, MetricKind.GpuMemUsedBytes,
        }
    };

    public NvmlLinuxProvider(ILogger<NvmlLinuxProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        try
        {
            _lib = NativeLibrary.Load("libnvidia-ml.so.1");
            if (_lib == nint.Zero) return ValueTask.CompletedTask;

            _init     = Bind<NvmlInit>("nvmlInit_v2");
            _shutdown = Bind<NvmlShutdown>("nvmlShutdown");
            _getHandle= Bind<NvmlDeviceGetHandleByIndex>("nvmlDeviceGetHandleByIndex_v2");
            _getUtil  = Bind<NvmlDeviceGetUtilizationRates>("nvmlDeviceGetUtilizationRates");
            _getTemp  = Bind<NvmlDeviceGetTemperature>("nvmlDeviceGetTemperature");
            _getClock = Bind<NvmlDeviceGetClockInfo>("nvmlDeviceGetClockInfo");
            _getPower = Bind<NvmlDeviceGetPowerUsage>("nvmlDeviceGetPowerUsage");
            _getFan   = Bind<NvmlDeviceGetFanSpeedEx>("nvmlDeviceGetFanSpeedEx");
            _getMem   = Bind<NvmlDeviceGetMemoryInfo>("nvmlDeviceGetMemoryInfo");
            _getName  = Bind<NvmlDeviceGetName>("nvmlDeviceGetName");

            if (_init!() != 0) return ValueTask.CompletedTask;
            if (_getHandle!(0, out _device) != 0) return ValueTask.CompletedTask;

            var buf = new byte[64];
            if (_getName!(_device, buf, 64) == 0)
                _gpuName = System.Text.Encoding.ASCII.GetString(buf).TrimEnd('\0');

            _available = true;
            _log.LogInformation("NVML Linux initialized: {Name}", _gpuName);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "NVML Linux not available");
        }
        return ValueTask.CompletedTask;
    }

    private T Bind<T>(string sym) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, sym));

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;
        if (_getUtil!(_device, out var util) == 0) _gpuLoad = util.Gpu;
        if (_getTemp!(_device, out uint temp) == 0) _gpuTemp = temp;
        if (_getPower!(_device, out uint mw) == 0) _gpuPower = mw / 1000.0;
        if (_getClock!(_device, 0, out uint mhz) == 0) _gpuClock = mhz;
        if (_getFan!(_device, 0, out uint rpm) == 0) _gpuFan = rpm;
        if (_getMem!(_device, out var mem) == 0) _gpuMemUsed = mem.Used;
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available) { value = 0; return false; }
        value = kind switch
        {
            MetricKind.GpuLoad         => _gpuLoad,
            MetricKind.GpuTempCore     => _gpuTemp,
            MetricKind.GpuPower        => _gpuPower,
            MetricKind.GpuClock        => _gpuClock,
            MetricKind.GpuFanRpm       => _gpuFan,
            MetricKind.GpuMemUsedBytes => _gpuMemUsed,
            _                          => 0,
        };
        return kind is MetricKind.GpuLoad or MetricKind.GpuTempCore or MetricKind.GpuPower
                    or MetricKind.GpuClock or MetricKind.GpuFanRpm or MetricKind.GpuMemUsedBytes;
    }

    public HardwareIdentity? ResolveIdentity(string domain) =>
        domain == "GPU" && _available ? new HardwareIdentity("GPU", "NVIDIA", _gpuName) : null;

    public ValueTask DisposeAsync()
    {
        if (_available) { try { _shutdown!(); } catch { } }
        if (_lib != nint.Zero) NativeLibrary.Free(_lib);
        return ValueTask.CompletedTask;
    }
}
