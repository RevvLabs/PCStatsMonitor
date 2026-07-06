using LibreHardwareMonitor.Hardware;
using PCStatsMonitor.Core;
using PCStatsMonitor.Providers.Lhm;
using Xunit;

namespace PCStatsMonitor.Providers.Lhm.Tests;

public class LhmSensorMapTests
{
    [Theory]
    [InlineData(HardwareType.Cpu, SensorType.Temperature, "CPU Package", MetricKind.CpuTempPackage)]
    [InlineData(HardwareType.Cpu, SensorType.Load, "CPU Total",           MetricKind.CpuLoad)]
    [InlineData(HardwareType.Cpu, SensorType.Power, "CPU Package",        MetricKind.CpuPower)]
    [InlineData(HardwareType.GpuNvidia, SensorType.Load, "D3D 3D",        MetricKind.GpuLoad)]
    [InlineData(HardwareType.GpuNvidia, SensorType.Load, "GPU Core",      MetricKind.GpuLoad)]
    [InlineData(HardwareType.Memory, SensorType.Data, "Memory Used",      MetricKind.RamUsedBytes)]
    [InlineData(HardwareType.Storage, SensorType.Temperature, "Temperature", MetricKind.StorageTempC)]
    [InlineData(HardwareType.SuperIO, SensorType.Fan, "CPU Fan",          MetricKind.CpuFanRpm)]
    [InlineData(HardwareType.SuperIO, SensorType.Fan, "Fan #1",           MetricKind.CpuFanRpm)]
    [InlineData(HardwareType.SuperIO, SensorType.Fan, "Fan1",             MetricKind.CpuFanRpm)]
    [InlineData(HardwareType.Motherboard, SensorType.Fan, "Fan 1",        MetricKind.CpuFanRpm)]
    [InlineData(HardwareType.Motherboard, SensorType.Fan, "CPU Fan 1",    MetricKind.CpuFanRpm)]
    public void KnownSensors_MapCorrectly(HardwareType hw, SensorType st, string name, MetricKind expected)
    {
        bool found = LhmSensorMap.TryGetMetric(hw, st, name, out var kind);
        Assert.True(found, $"Sensor not mapped: {hw}/{st}/{name}");
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(HardwareType.Cpu, SensorType.Temperature, "Warning")]
    [InlineData(HardwareType.Cpu, SensorType.Temperature, "Hot Spot")]
    [InlineData(HardwareType.GpuNvidia, SensorType.Load, "D3D Video Decode")]
    public void UnknownOrExcludedSensors_NotMapped(HardwareType hw, SensorType st, string name)
    {
        bool found = LhmSensorMap.TryGetMetric(hw, st, name, out _);
        Assert.False(found, $"Should not be mapped: {hw}/{st}/{name}");
    }
}
