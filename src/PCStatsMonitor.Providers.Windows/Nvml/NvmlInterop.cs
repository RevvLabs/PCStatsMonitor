using System.Runtime.InteropServices;

namespace PCStatsMonitor.Providers.Windows.Nvml;

/// <summary>
/// Minimal P/Invoke bindings for NVML (NVIDIA Management Library).
/// Resolved at runtime from nvml.dll to avoid hard-link dependency.
/// Pinned to the NVML 11 baseline — stable across Turing and later.
/// </summary>
internal static class NvmlInterop
{
    private static nint _lib;

    // Custom delegates — Func<> cannot carry out/ref parameters
    private delegate int DInit();
    private delegate int DShutdown();
    private delegate int DDeviceGetHandleByIndex(uint idx, out nint handle);
    private delegate int DDeviceGetUtilizationRates(nint handle, out NvmlUtilization util);
    private delegate int DDeviceGetTemperature(nint handle, NvmlTemperatureSensor sensor, out uint temp);
    private delegate int DDeviceGetClockInfo(nint handle, NvmlClockType type, out uint mhz);
    private delegate int DDeviceGetPowerUsage(nint handle, out uint mw);
    private delegate int DDeviceGetMemoryInfo(nint handle, out NvmlMemory mem);
    private delegate int DDeviceGetName(nint handle, byte[] buf, uint len);
    private delegate int DDeviceGetCount(out uint count);

    private static DInit? _init;
    private static DShutdown? _shutdown;
    private static DDeviceGetHandleByIndex? _getHandle;
    private static DDeviceGetUtilizationRates? _getUtil;
    private static DDeviceGetTemperature? _getTemp;
    private static DDeviceGetClockInfo? _getClock;
    private static DDeviceGetPowerUsage? _getPower;
    private static DDeviceGetMemoryInfo? _getMem;
    private static DDeviceGetName? _getName;
    private static DDeviceGetCount? _getCount;

    public static bool IsLoaded { get; private set; }

    public static bool TryLoad() => TryLoad(out _);

    public static bool TryLoad(out string? error)
    {
        error = null;
        if (IsLoaded) return true;
        try
        {
            _lib = NativeLibrary.Load("nvml.dll");
            if (_lib == nint.Zero) { error = "NativeLibrary.Load returned null handle"; return false; }

            _init     = Bind<DInit>("nvmlInit_v2");
            _shutdown = Bind<DShutdown>("nvmlShutdown");
            _getHandle= Bind<DDeviceGetHandleByIndex>("nvmlDeviceGetHandleByIndex_v2");
            _getUtil  = Bind<DDeviceGetUtilizationRates>("nvmlDeviceGetUtilizationRates");
            _getTemp  = Bind<DDeviceGetTemperature>("nvmlDeviceGetTemperature");
            _getClock = Bind<DDeviceGetClockInfo>("nvmlDeviceGetClockInfo");
            _getPower = Bind<DDeviceGetPowerUsage>("nvmlDeviceGetPowerUsage");
            _getMem   = Bind<DDeviceGetMemoryInfo>("nvmlDeviceGetMemoryInfo");
            _getName  = Bind<DDeviceGetName>("nvmlDeviceGetName");
            _getCount = Bind<DDeviceGetCount>("nvmlDeviceGetCount_v2");

            IsLoaded = true;
            return true;
        }
        catch (Exception ex)
        {
            // Distinguish "dll missing" from "dll loaded but an export is absent" —
            // older drivers ship nvml.dll without some newer entry points
            error = ex.Message;
            if (_lib != nint.Zero) { NativeLibrary.Free(_lib); _lib = nint.Zero; }
            return false;
        }
    }

    private static T Bind<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));

    public static int Init() => _init?.Invoke() ?? -1;
    public static int Shutdown() => _shutdown?.Invoke() ?? -1;

    public static int DeviceGetHandleByIndex(uint index, out nint handle)
    {
        handle = nint.Zero;
        return _getHandle?.Invoke(index, out handle) ?? -1;
    }

    public static int DeviceGetUtilizationRates(nint handle, out NvmlUtilization util)
    {
        util = default;
        return _getUtil?.Invoke(handle, out util) ?? -1;
    }

    public static int DeviceGetTemperature(nint handle, out uint temp)
    {
        temp = 0;
        return _getTemp?.Invoke(handle, NvmlTemperatureSensor.Gpu, out temp) ?? -1;
    }

    public static int DeviceGetClockInfo(nint handle, NvmlClockType type, out uint mhz)
    {
        mhz = 0;
        return _getClock?.Invoke(handle, type, out mhz) ?? -1;
    }

    public static int DeviceGetPowerUsage(nint handle, out uint mw)
    {
        mw = 0;
        return _getPower?.Invoke(handle, out mw) ?? -1;
    }

    public static int DeviceGetMemoryInfo(nint handle, out NvmlMemory mem)
    {
        mem = default;
        return _getMem?.Invoke(handle, out mem) ?? -1;
    }

    public static int DeviceGetName(nint handle, byte[] name, uint len) =>
        _getName?.Invoke(handle, name, len) ?? -1;

    public static void Unload()
    {
        if (_lib != nint.Zero)
        {
            NativeLibrary.Free(_lib);
            _lib = nint.Zero;
            IsLoaded = false;
        }
    }
}

public enum NvmlClockType : uint { Graphics = 0, SM = 1, Mem = 2, Video = 3 }

// NVML_TEMPERATURE_GPU — the only sensor index NVML defines; core die temperature
public enum NvmlTemperatureSensor : uint { Gpu = 0 }

[StructLayout(LayoutKind.Sequential)]
public struct NvmlUtilization { public uint Gpu; public uint Memory; }

[StructLayout(LayoutKind.Sequential)]
public struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }
