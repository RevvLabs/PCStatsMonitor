using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PCStatsMonitor.Core;
using PCStatsMonitor.Providers.Lhm;

namespace PCStatsMonitor.App.Bootstrapping;

/// <summary>
/// Assembles the ordered provider list based on the current OS.
/// Providers earlier in the list take precedence for each MetricKind.
/// </summary>
public static class ProviderFactory
{
    public static IReadOnlyList<ISensorProvider> Create(IServiceProvider services)
    {
        var providers = new List<ISensorProvider>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // NVML first for GPU — canonical load, no D3D string matching
            providers.Add(CreateNvmlWindows(services));
            // Windows native memory — accurate physical RAM (LHM reports committed/virtual bytes)
            providers.Add(CreateWindowsMemory(services));
            // LHM for everything else (CPU, motherboard fan, storage temp)
            providers.Add(services.GetRequiredService<LhmSensorProvider>());
            // PDH as CPU load fallback
            providers.Add(CreatePdhCpu(services));
            // PDH thermal fallback for CPUs LHM can't read (e.g. Intel Arrow Lake)
            providers.Add(CreatePdhCpuThermal(services));
            // WMI ACPI thermal zone — second temp fallback when PDH has no thermal counters
            providers.Add(CreateWmiCpuTemp(services));
            // EMI energy meter — CPU power fallback when LHM's ring0 driver is blocked
            providers.Add(CreateEmiCpuPower(services));
            // PDH processor performance — CPU clock fallback (no ring0 required)
            providers.Add(CreatePdhCpuClock(services));
            // PDH physical disk — storage activity fallback when LHM has no Total Activity sensor
            providers.Add(CreatePdhDiskActivity(services));
            // IOCTL fallback for SSD/NVMe temp when LHM can't read it
            providers.Add(CreateStorageTemp(services));
            // WMI per-drive inventory — feeds the multi-drive carousel (SensorSnapshot.Drives)
            providers.Add(CreateStorageInventory(services));
        }
        else // Linux
        {
            // NVML for NVIDIA GPU
            providers.Add(CreateNvmlLinux(services));
            // sysfs for AMD GPU
            providers.Add(CreateSysfsGpu(services));
            // LHM where it works on Linux
            providers.Add(services.GetRequiredService<LhmSensorProvider>());
            // Native Linux providers (take precedence where LHM is weak on Linux)
            providers.Add(CreateProcStat(services));
            providers.Add(CreateHwmon(services));
            providers.Add(CreateRapl(services));
            providers.Add(CreateMeminfo(services));
        }

        // DriveInfo is cross-platform — always available via Core, added last for both OSes
        providers.Add(ActivatorUtilities.CreateInstance<DriveInfoStorageProvider>(services));

        // Drop platform placeholders so the pump never polls a no-op provider
        return providers.Where(p => p is not NullProvider).ToList();
    }

    // Dynamic type loading so the Windows project is not compiled on Linux and vice versa
    private static ISensorProvider CreateWindowsMemory(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Windows.WindowsMemoryProvider, PCStatsMonitor.Providers.Windows");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreateNvmlWindows(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Windows.Nvml.NvmlGpuProvider, PCStatsMonitor.Providers.Windows");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreatePdhCpu(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Windows.PdhCpuProvider, PCStatsMonitor.Providers.Windows");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreatePdhCpuThermal(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Windows.PdhCpuThermalProvider, PCStatsMonitor.Providers.Windows");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreateWmiCpuTemp(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Windows.WmiCpuTempProvider, PCStatsMonitor.Providers.Windows");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreateEmiCpuPower(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Windows.EmiCpuPowerProvider, PCStatsMonitor.Providers.Windows");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreatePdhCpuClock(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Windows.PdhCpuClockProvider, PCStatsMonitor.Providers.Windows");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreatePdhDiskActivity(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Windows.PdhDiskActivityProvider, PCStatsMonitor.Providers.Windows");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreateStorageTemp(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Windows.StorageTempProvider, PCStatsMonitor.Providers.Windows");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreateStorageInventory(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Windows.StorageInventoryProvider, PCStatsMonitor.Providers.Windows");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreateNvmlLinux(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Linux.NvmlLinuxProvider, PCStatsMonitor.Providers.Linux");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreateSysfsGpu(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Linux.SysfsGpuProvider, PCStatsMonitor.Providers.Linux");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreateProcStat(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Linux.ProcStatCpuProvider, PCStatsMonitor.Providers.Linux");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreateHwmon(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Linux.HwmonProvider, PCStatsMonitor.Providers.Linux");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreateRapl(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Linux.RaplProvider, PCStatsMonitor.Providers.Linux");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

    private static ISensorProvider CreateMeminfo(IServiceProvider s)
    {
        var type = Type.GetType("PCStatsMonitor.Providers.Linux.MeminfoProvider, PCStatsMonitor.Providers.Linux");
        return type != null
            ? (ISensorProvider)ActivatorUtilities.CreateInstance(s, type)
            : NullProvider.Instance;
    }

}

/// <summary>No-op provider used when a platform assembly isn't available.</summary>
file sealed class NullProvider : ISensorProvider
{
    public static readonly NullProvider Instance = new();
    public ProviderCapabilities Capabilities { get; } = new()
    {
        Name = "Null", IsThreadSafe = true,
        SupportedMetrics = new HashSet<MetricKind>(),
    };
    public ValueTask InitializeAsync(CancellationToken ct) => ValueTask.CompletedTask;
    public void Refresh(MetricGroup group) { }
    public bool TryRead(MetricKind kind, out double value) { value = 0; return false; }
    public HardwareIdentity? ResolveIdentity(string domain) => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
