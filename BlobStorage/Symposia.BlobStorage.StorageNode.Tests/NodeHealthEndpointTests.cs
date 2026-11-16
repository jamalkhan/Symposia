using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Symposia.BlobStorage.Protocol;
using Symposia.BlobStorage.StorageNode.Health;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>Storage node liveness, readiness, metrics and drain (issue #1 FR-23, FR-25, FR-26, FR-28).</summary>
public sealed class NodeHealthEndpointTests : IDisposable
{
    private const string Token = "node-operator-token";
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_ReturnsOkWithNodeIdVersionAndUptime()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.StartsWith("0x", body.RootElement.GetProperty("node_id").GetString());
        Assert.True(body.RootElement.TryGetProperty("version", out _));
        Assert.True(body.RootElement.TryGetProperty("uptime_seconds", out _));
    }

    [Fact]
    public async Task Ready_WithNothingToWaitFor_IsReadyWithTheDocumentedShape()
    {
        var response = await _factory.CreateClient().GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal("ready", root.GetProperty("status").GetString());
        Assert.Equal("us-east", root.GetProperty("region").GetString());
        Assert.Equal(2, root.GetProperty("tier").GetInt32());
        foreach (var field in new[] { "node_id", "penalty_stage", "storage_available_bytes", "storage_used_bytes", "peer_count", "region_verified" })
        {
            Assert.True(root.TryGetProperty(field, out _), field);
        }
    }

    [Fact]
    public void NotReadyReason_CoversEachDocumentedCondition()
    {
        var storage = _factory.Services.GetRequiredService<StorageWritableMonitor>();
        var running = new NodeShutdownState();
        var options = new StorageNodeOptions();

        var notJoined = new NodeNetworkStatus();
        notJoined.ConfirmRegistration();
        Assert.Equal("network_not_joined", NodeHealthEndpoints.NotReadyReason(notJoined, options, storage, running)?.Reason);

        var unregistered = new NodeNetworkStatus();
        unregistered.MarkNetworkJoined();
        Assert.Equal("registration_not_confirmed", NodeHealthEndpoints.NotReadyReason(unregistered, options, storage, running)?.Reason);

        var joined = new NodeNetworkStatus();
        joined.MarkNetworkJoined();
        joined.ConfirmRegistration();
        Assert.Equal("region_verification_pending", NodeHealthEndpoints.NotReadyReason(joined, new StorageNodeOptions { RequireRegionVerification = true },
            storage, running)?.Reason);
        Assert.Null(NodeHealthEndpoints.NotReadyReason(joined, new StorageNodeOptions { RequireRegionVerification = true, RegionVerified = true },
            storage, running));

        var draining = new NodeShutdownState();
        draining.BeginDrain();
        Assert.Equal("draining", NodeHealthEndpoints.NotReadyReason(joined, options, storage, draining)?.Reason);
    }

    [Fact]
    public async Task Ready_StoragePathNotWritable_Returns503()
    {
        var root = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageNodeOptions>>().Value.StorageRoot;
        Directory.Delete(root, recursive: true);
        File.WriteAllText(root, "a file where the blob directory should be");
        _factory.Services.GetRequiredService<StorageWritableMonitor>().Check();

        var response = await _factory.CreateClient().GetAsync("/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("storage_not_writable", body.RootElement.GetProperty("reason").GetString());
        File.Delete(root);
    }

    [Fact]
    public async Task Metrics_RequiresTheOperatorToken_AndExposesEveryDocumentedFamily()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/metrics")).StatusCode);

        // One write and one read, so the I/O counters move.
        var grpc = new Protocol.StorageNode.StorageNodeClient(GrpcChannel.ForAddress(_factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() }));
        using (var write = grpc.WriteBlob())
        {
            await write.RequestStream.WriteAsync(new WriteBlobChunk { Header = new WriteBlobHeader { TenantId = "t", Bucket = "b", Key = "k" } });
            await write.RequestStream.WriteAsync(new WriteBlobChunk { Data = ByteString.CopyFrom(new byte[1000]) });
            await write.RequestStream.CompleteAsync();
            var written = await write.ResponseAsync;
            using var read = grpc.ReadBlob(new ReadBlobRequest { Cid = written.Cid });
            while (await read.ResponseStream.MoveNext(CancellationToken.None)) { }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Token) } };
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var family in new[]
                 {
                     "node_storage_available_bytes", "node_storage_used_bytes", "node_blob_count",
                     "node_read_throughput_bytes_per_second", "node_write_throughput_bytes_per_second", "node_iops_read", "node_iops_write",
                     "node_io_queue_depth", "node_network_inbound_bytes_total", "node_network_outbound_bytes_total", "node_peer_latency_seconds",
                     "node_replication_tasks_active", "node_replication_bytes_total", "node_integrity_check_failures_total",
                     "node_penalty_stage", "node_uptime_pct", "node_heartbeat_compliance_pct",
                     "node_smart_reallocated_sectors", "node_smart_pending_sectors", "node_smart_uncorrectable_errors",
                     "node_smart_temperature_celsius", "node_smart_wear_level_pct",
                 })
        {
            Assert.Contains($"# TYPE {family} ", text);
        }

        Assert.Contains("node_network_inbound_bytes_total 1000", text);
        Assert.Contains("node_network_outbound_bytes_total 1000", text);
        Assert.Contains("node_blob_count 1", text);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"symposia-node-health-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageNode:StorageRoot"] = Path.Combine(_dataDir, "blobs"),
                ["StorageNode:ManifestDbPath"] = Path.Combine(_dataDir, "manifest.db"),
                ["StorageNode:MaxCapacityBytes"] = "1073741824",
                ["StorageNode:NodeIdentityKeyPath"] = Path.Combine(_dataDir, "node-identity.pem"),
                ["StorageNode:MetricsBearerToken"] = Token,
                ["StorageNode:Region"] = "us-east",
                ["StorageNode:Tier"] = "2",
            }));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_dataDir))
            {
                Directory.Delete(_dataDir, recursive: true);
            }
        }
    }
}
