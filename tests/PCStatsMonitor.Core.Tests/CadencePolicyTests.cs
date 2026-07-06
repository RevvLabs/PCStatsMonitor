using PCStatsMonitor.Core;
using Xunit;

namespace PCStatsMonitor.Core.Tests;

public class CadencePolicyTests
{
    private readonly CadencePolicy _policy = new();

    [Theory]
    [InlineData(1,  new[] { MetricGroup.Fast })]
    [InlineData(2,  new[] { MetricGroup.Fast, MetricGroup.Medium })]
    [InlineData(5,  new[] { MetricGroup.Fast, MetricGroup.Slow })]
    [InlineData(10, new[] { MetricGroup.Fast, MetricGroup.Medium, MetricGroup.Slow, MetricGroup.Idle })]
    public void GroupsDueOnTick_ReturnsExpectedGroups(long tick, MetricGroup[] expected)
    {
        var due = _policy.GroupsDueOnTick(tick).ToHashSet();
        foreach (var g in expected)
            Assert.Contains(g, due);
    }

    [Fact]
    public void GetGroup_ReturnsCorrectGroup()
    {
        Assert.Equal(MetricGroup.Fast,   _policy.GetGroup(MetricKind.CpuLoad));
        Assert.Equal(MetricGroup.Medium, _policy.GetGroup(MetricKind.CpuFanRpm));
        Assert.Equal(MetricGroup.Slow,   _policy.GetGroup(MetricKind.StorageTempC));
        Assert.Equal(MetricGroup.Idle,   _policy.GetGroup(MetricKind.RamTotalBytes));
    }
}
