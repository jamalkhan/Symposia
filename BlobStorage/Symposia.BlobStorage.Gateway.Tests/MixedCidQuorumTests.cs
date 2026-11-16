using System.Net;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>A node storing different bytes than the others neither completes nor decides a quorum (#7; review: Lenny #6).</summary>
[Collection("GatewayIntegration")]
public sealed class MixedCidQuorumTests
{
    private static readonly Dictionary<string, string?> Network = new() { ["Gateway:Redundancy:Mode"] = "Network" };

    private static Func<int, ProbeResponse> Regions(params string[] regions) =>
        i => new ProbeResponse { Healthy = true, AvailableStorageBytes = (1L << 30) - i, Region = regions[i] };

    [Fact]
    public async Task FaultyNodeAckingFirst_DoesNotDecideTheContent_AndGoodNodesStillMakeQuorum()
    {
        // Node 0 corrupts what it stores and answers at once; the three good ones answer after a delay.
        var slow = Enumerable.Range(0, 4).Select(_ => new DelayAckHandler(TimeSpan.FromMilliseconds(300))).ToList();
        using var cluster = await TestCluster.StartAsync(4, Network,
            handler: i => i == 0 ? new CorruptWritesHandler() : slow[i], probe: Regions("east", "west", "central", "south"));
        var body = "the real content "u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();

        var etag = await cluster.PutAsync("mixed/k", body);

        var record = cluster.Services.GetRequiredService<GatewayMetadataStore>().GetObject(TestCluster.Bucket, "mixed/k")!;
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)), record.Cid);
        Assert.Equal(record.Cid, etag);
        Assert.DoesNotContain(cluster.Connections[0].Id, record.NodeIds);
        Assert.Equal(body, await cluster.GetAsync("mixed/k"));
    }

    [Fact]
    public async Task BadCidAlone_NeverMeetsQuorum_EvenWhenFaultyNodesAgree()
    {
        // Three of four nodes corrupt the bytes identically, so they agree with each other on the wrong CID;
        // only one node holds what the client sent. The gateway counts acks against its own hash of the body.
        using var cluster = await TestCluster.StartAsync(4, Network,
            handler: i => i < 3 ? new CorruptWritesHandler() : null, probe: Regions("east", "west", "central", "south"));
        var body = "content"u8.ToArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();

        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() => cluster.PutAsync("mixed/bad", body));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Null(cluster.Services.GetRequiredService<GatewayMetadataStore>().GetObject(TestCluster.Bucket, "mixed/bad"));
    }
}

/// <summary>Flips one byte of every data chunk a node is sent, so it stores (and reports) a different CID.</summary>
internal sealed class CorruptWritesHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/WriteBlob", StringComparison.Ordinal) == true && request.Content is { } content)
        {
            request.Content = new CorruptingContent(content);
        }

        return base.SendAsync(request, ct);
    }

    private sealed class CorruptingContent(HttpContent inner) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await using var source = await inner.ReadAsStreamAsync();
            var buffer = new byte[64 * 1024];
            int read;
            long total = 0;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                // gRPC frames: a 5-byte prefix, then the protobuf. Past the first frame (the header), flip the
                // last byte of each read, which lands in chunk data, keeping framing and lengths intact.
                if (total > 64 && read > 8) buffer[read - 1] ^= 0xFF;
                total += read;
                await stream.WriteAsync(buffer.AsMemory(0, read));
                await stream.FlushAsync();
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
