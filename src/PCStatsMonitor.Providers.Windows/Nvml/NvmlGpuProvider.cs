using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.Providers.Windows.Nvml;

/// <summary>
/// NVIDIA GPU sensor provider using NVML directly.
/// Eliminates the "GPU Core vs D3D 3D vs Hot Spot" ambiguity in LHM — NVML returns
/// canonical nvmlUtilization_t.gpu which is the true 3D engine utilization.
///
/// Loaded at runtime via NativeLibrary (no compile-time nvml.dll dependency).
/// IsThreadSafe = true: NVML is safe for concurrent calls after nvmlInit.
/// </summary>
public sealed class NvmlGpuProvider : ISensorProvider
{
    private readonly ILogger<NvmlGpuProvider> _log;
    private nint _deviceHandle;
    private string _gpuName = "NVIDIA GPU";
    private bool _available;

    private double _gpuLoad;
    private double _gpuTemp;
    private double _gpuPower;
    private double _gpuClock;
    private double _gpuMemUsed;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "NVML (NVIDIA)",
        IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind>
        {
            MetricKind.GpuTempCore,
            MetricKind.GpuPower,
            MetricKind.GpuClock,
            MetricKind.GpuMemUsedBytes,
        }
    };

    public NvmlGpuProvider(ILogger<NvmlGpuProvider> log) => _log = log;

    public ValueTask InitializeAsync(CancellationToken ct)
    {
        if (!NvmlInterop.TryLoad(out string? loadError))
        {
            _log.LogInformation("NVML unavailable ({Reason}) — NVML GPU provider disabled",
                loadError ?? "nvml.dll not found");
            return ValueTask.CompletedTask;
        }

        int r = NvmlInterop.Init();
        if (r != 0)
        {
            _log.LogWarning("nvmlInit returned {Code} — NVML GPU provider disabled", r);
            return ValueTask.CompletedTask;
        }

        r = NvmlInterop.DeviceGetHandleByIndex(0, out _deviceHandle);
        if (r != 0)
        {
            _log.LogWarning("nvmlDeviceGetHandleByIndex(0) returned {Code}", r);
            return ValueTask.CompletedTask;
        }

        // Read GPU name
        var nameBuf = new byte[64];
        if (NvmlInterop.DeviceGetName(_deviceHandle, nameBuf, (uint)nameBuf.Length) == 0)
            _gpuName = System.Text.Encoding.ASCII.GetString(nameBuf).TrimEnd('\0');

        _available = true;
        _log.LogInformation("NVML initialized for GPU: {Name}", _gpuName);
        return ValueTask.CompletedTask;
    }

    public void Refresh(MetricGroup group)
    {
        if (!_available) return;

        if (NvmlInterop.DeviceGetUtilizationRates(_deviceHandle, out var util) == 0)
            _gpuLoad = util.Gpu;

        int tempR = NvmlInterop.DeviceGetTemperature(_deviceHandle, out uint temp);
        if (tempR == 0)
            _gpuTemp = temp;
        else
            _log.LogDebug("nvmlDeviceGetTemperature returned {Code}", tempR);

        if (NvmlInterop.DeviceGetPowerUsage(_deviceHandle, out uint mw) == 0)
            _gpuPower = mw / 1000.0; // mW → W

        if (NvmlInterop.DeviceGetClockInfo(_deviceHandle, NvmlClockType.Graphics, out uint mhz) == 0)
            _gpuClock = mhz;

        if (NvmlInterop.DeviceGetMemoryInfo(_deviceHandle, out var mem) == 0)
            _gpuMemUsed = mem.Used;
    }

    public bool TryRead(MetricKind kind, out double value)
    {
        if (!_available) { value = 0; return false; }

        // Fan is deliberately absent: NVML reports fan duty in %, not RPM, and laptop GPUs
        // don't expose it at all — LHM's RPM sensor is the only correct source for GpuFanRpm
        //
        // GpuLoad is allowed through at 0 (idle is real data); the rest require a nonzero
        // reading so a failed NVML call (e.g. unsupported sensor) falls through to the LHM
        // fallback instead of shadowing it with a stale/never-populated 0.
        value = kind switch
        {
            MetricKind.GpuLoad         => _gpuLoad,
            MetricKind.GpuTempCore     => _gpuTemp,
            MetricKind.GpuPower        => _gpuPower,
            MetricKind.GpuClock        => _gpuClock,
            MetricKind.GpuMemUsedBytes => _gpuMemUsed,
            _                          => 0,
        };
        return kind switch
        {
            MetricKind.GpuLoad         => true,
            MetricKind.GpuTempCore     => _gpuTemp > 0,
            MetricKind.GpuPower        => _gpuPower > 0,
            MetricKind.GpuClock        => _gpuClock > 0,
            MetricKind.GpuMemUsedBytes => _gpuMemUsed > 0,
            _                          => false,
        };
    }

    public HardwareIdentity? ResolveIdentity(string domain) =>
        domain == "GPU" && _available
            ? new HardwareIdentity("GPU", "NVIDIA", _gpuName)
            : null;

    public ValueTask DisposeAsync()
    {
        if (_available)
        {
            try { NvmlInterop.Shutdown(); } catch { }
            NvmlInterop.Unload();
        }
        return ValueTask.CompletedTask;
    }
}
