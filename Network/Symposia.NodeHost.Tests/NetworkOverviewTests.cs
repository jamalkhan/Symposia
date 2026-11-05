using Symposia.Network.Node;
using Symposia.NodeHost.Diagnostics;

namespace Symposia.NodeHost.Tests;

/// <summary>Issue #145 FR6: the Network tab shows readiness for scheduled protocol activations (QA TC-34, TC-35).</summary>
public sealed class NetworkOverviewTests
{
    private static readonly long Past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
    private static readonly long Future = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();

    private static NodeServices Storage(params string[] features) =>
        new("0xA", new NodeServices.StorageService("http://10.0.0.1:6180"), 1, features);

    private static ProtocolSchedule Schedule(params ProtocolSchedule.Activation[] features) =>
        new(true, 0, new ProtocolSchedule.MinVersion(0, 0, 0), features);

    private static NetworkNodeRow Row(NodeServices? services) =>
        new("0xA", null, null, false, true, new StorageHealth(true, HealthLevel.Ok, null, null, null),
            new ComputeHealth(false, HealthLevel.Off, null, null)) { Services = services };

    [Fact]
    public void Unready_FlagsUpcomingAndActiveStorageActivations()
    {
        var schedule = Schedule(new(ProtocolFeatures.StorageGrpcTls, Future), new("storage.other", Past), new("compute.x", Future));
        var warnings = NetworkOverview.Unready(Storage(ProtocolFeatures.StorageGrpcPlaintext), schedule);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("Will be refused") && w.Contains(ProtocolFeatures.StorageGrpcTls));
        // Same judgement as gateways: a pre-#145 document is read by what it serves.
        var legacy = new NodeServices("0xA", new NodeServices.StorageService("https://10.0.0.1:6180"));
        Assert.Empty(NetworkOverview.Unready(legacy, Schedule(new ProtocolSchedule.Activation(ProtocolFeatures.StorageGrpcTls, Past))));
        Assert.Contains(warnings, w => w.StartsWith("Refused by gateways") && w.Contains("storage.other"));
    }

    [Fact]
    public void Unready_IgnoresNodesWithoutStorage_AndReadyNodes()
    {
        var schedule = Schedule(new ProtocolSchedule.Activation(ProtocolFeatures.StorageGrpcTls, Future));
        Assert.Empty(NetworkOverview.Unready(null, schedule));
        Assert.Empty(NetworkOverview.Unready(Storage(ProtocolFeatures.StorageGrpcTls), schedule));
    }

    [Fact]
    public void Readiness_CountsStorageNodesAdvertisingEachScheduledFeature()
    {
        var rows = new[] { Row(Storage(ProtocolFeatures.StorageGrpcTls)), Row(Storage()), Row(null) };
        var readiness = NetworkOverview.Readiness(rows, Schedule(new(ProtocolFeatures.StorageGrpcTls, Future), new("storage.cancelled", 0)));

        var tls = Assert.Single(readiness);
        Assert.Equal((1, 2, false), (tls.Ready, tls.Total, tls.InEffect));
    }
}
