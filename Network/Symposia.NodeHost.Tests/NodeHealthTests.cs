using Symposia.NodeHost.Diagnostics;

namespace Symposia.NodeHost.Tests;

/// <summary>Network tab indicator thresholds.</summary>
public sealed class NodeHealthTests
{
    private const long Capacity = 100;

    [Theory]
    [InlineData(100, HealthLevel.Ok)]
    [InlineData(40, HealthLevel.Ok)]
    [InlineData(39, HealthLevel.Warn)]
    [InlineData(21, HealthLevel.Warn)]
    [InlineData(20, HealthLevel.Bad)]
    [InlineData(0, HealthLevel.Bad)]
    public void Storage_level_follows_free_space(long free, HealthLevel expected) =>
        Assert.Equal(expected, NodeHealth.StorageLevel(free, Capacity));

    [Fact]
    public void Storage_with_no_capacity_is_bad() =>
        Assert.Equal(HealthLevel.Bad, NodeHealth.StorageLevel(0, 0));

    [Theory]
    [InlineData(0, HealthLevel.Ok)]
    [InlineData(20, HealthLevel.Ok)]
    [InlineData(20.1, HealthLevel.Warn)]
    [InlineData(50, HealthLevel.Warn)]
    [InlineData(50.1, HealthLevel.Bad)]
    public void Compute_level_follows_cpu(double cpu, HealthLevel expected) =>
        Assert.Equal(expected, NodeHealth.ComputeLevel(cpu));
}
