using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Symposia.BlobStorage.Protocol;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>
/// Integration tests that boot the full StorageNode ASP.NET Core host in-process and exercise
/// all five gRPC operations against a temporary directory that is cleaned up after each test.
/// </summary>
public sealed class StorageNodeIntegrationTests : IClassFixture<StorageNodeIntegrationTests.NodeFactory>, IDisposable
{
    private readonly NodeFactory _factory;
    private readonly GrpcChannel _channel;
    private readonly Protocol.StorageNode.StorageNodeClient _client;

    public StorageNodeIntegrationTests(NodeFactory factory)
    {
        _factory = factory;
        var httpClient = factory.CreateClient();
        _channel = GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = httpClient,
        });
        _client = new Protocol.StorageNode.StorageNodeClient(_channel);
    }

    [Fact]
    public async Task WriteBlob_SmallPayload_ReturnsCidAndSize()
    {
        var payload = "hello, symposia network"u8.ToArray();
        var expectedCid = ComputeCid(payload);

        var call = _client.WriteBlob();
        await call.RequestStream.WriteAsync(new WriteBlobChunk
        {
            Header = new WriteBlobHeader { TenantId = "tenant-1", Bucket = "bucket-a", Key = "test.txt" },
        });
        await call.RequestStream.WriteAsync(new WriteBlobChunk { Data = ByteString.CopyFrom(payload) });
        await call.RequestStream.CompleteAsync();

        var response = await call.ResponseAsync;

        Assert.Equal(expectedCid, response.Cid);
        Assert.Equal(payload.Length, response.SizeBytes);
    }

    [Fact]
    public async Task WriteBlob_ThenReadBlob_ReturnsIdenticalBytes()
    {
        var payload = Encoding.UTF8.GetBytes("round-trip test payload");

        var writeCid = await WriteBlobAsync("tenant-1", "bucket-a", "roundtrip.txt", payload);

        var collected = await ReadBlobAsync(writeCid);
        Assert.Equal(payload, collected);
    }

    [Fact]
    public async Task WriteBlob_ThenReadBlobWithRange_ReturnsSlice()
    {
        var payload = "0123456789abcdef"u8.ToArray();
        var cid = await WriteBlobAsync("tenant-1", "bucket-a", "range.bin", payload);

        var slice = await ReadBlobAsync(cid, offset: 4, length: 6);
        Assert.Equal("456789"u8.ToArray(), slice);
    }

    [Fact]
    public async Task WriteBlob_ContentAddressedDedup_BothCallsReturnSameCid()
    {
        var payload = "duplicate content"u8.ToArray();
        var cid1 = await WriteBlobAsync("tenant-1", "bucket-a", "dup1.bin", payload);
        var cid2 = await WriteBlobAsync("tenant-1", "bucket-a", "dup2.bin", payload);
        Assert.Equal(cid1, cid2);
    }

    [Fact]
    public async Task DeleteBlob_ExistingBlob_ReturnsTrueAndBlobIsGone()
    {
        var payload = "to be deleted"u8.ToArray();
        var cid = await WriteBlobAsync("tenant-1", "bucket-a", "delete-me.txt", payload);

        var deleteResponse = await _client.DeleteBlobAsync(new DeleteBlobRequest { Cid = cid });
        Assert.True(deleteResponse.Deleted);

        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() =>
            ReadBlobAsync(cid));
    }

    [Fact]
    public async Task Probe_ReturnsNodeIdAndPositiveBlobCount()
    {
        await WriteBlobAsync("tenant-1", "bucket-a", "probe-seed.txt", "probe"u8.ToArray());

        var probe = await _client.ProbeAsync(new ProbeRequest());

        Assert.NotEmpty(probe.NodeId);
        Assert.True(probe.BlobCount >= 1);
        Assert.True(probe.UsedStorageBytes > 0);
        Assert.True(probe.Healthy);
    }

    [Fact]
    public async Task IntegrityChallenge_WholeBlob_MatchesSha256()
    {
        var payload = "integrity challenge payload"u8.ToArray();
        var cid = await WriteBlobAsync("tenant-1", "bucket-a", "challenge.bin", payload);
        var expected = Convert.ToHexStringLower(SHA256.HashData(payload));

        var response = await _client.IntegrityChallengeAsync(
            new IntegrityChallengeRequest { Cid = cid, Offset = 0, Length = 0 });

        Assert.Equal(expected, response.Sha256Hex);
    }

    [Fact]
    public async Task IntegrityChallenge_ByteRange_MatchesSha256OfSlice()
    {
        var payload = "0123456789"u8.ToArray();
        var cid = await WriteBlobAsync("tenant-1", "bucket-a", "range-challenge.bin", payload);
        var expected = Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan(2, 4)));

        var response = await _client.IntegrityChallengeAsync(
            new IntegrityChallengeRequest { Cid = cid, Offset = 2, Length = 4 });

        Assert.Equal(expected, response.Sha256Hex);
    }

    [Fact]
    public async Task HealthLive_ReturnsOk()
    {
        var http = _factory.CreateClient();
        var response = await http.GetAsync("/healthz/live");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task HealthReady_ReturnsOk()
    {
        var http = _factory.CreateClient();
        var response = await http.GetAsync("/healthz/ready");
        response.EnsureSuccessStatusCode();
    }

    // Helpers

    private async Task<string> WriteBlobAsync(string tenantId, string bucket, string key, byte[] payload)
    {
        var call = _client.WriteBlob();
        await call.RequestStream.WriteAsync(new WriteBlobChunk
        {
            Header = new WriteBlobHeader { TenantId = tenantId, Bucket = bucket, Key = key },
        });
        await call.RequestStream.WriteAsync(new WriteBlobChunk { Data = ByteString.CopyFrom(payload) });
        await call.RequestStream.CompleteAsync();
        var response = await call.ResponseAsync;
        return response.Cid;
    }

    private async Task<byte[]> ReadBlobAsync(string cid, long offset = 0, long length = 0)
    {
        var call = _client.ReadBlob(new ReadBlobRequest { Cid = cid, Offset = offset, Length = length });
        var buffer = new List<byte>();
        await foreach (var chunk in call.ResponseStream.ReadAllAsync())
        {
            buffer.AddRange(chunk.Data.ToByteArray());
        }

        return [.. buffer];
    }

    private static string ComputeCid(byte[] data)
    {
        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    public void Dispose() => _channel.Dispose();

    /// <summary>Spins up a StorageNode in a temp directory, cleaned up after the test class run.</summary>
    public sealed class NodeFactory : WebApplicationFactory<Program>, IDisposable
    {
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"symposia-node-test-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["StorageNode:StorageRoot"] = Path.Combine(_dataDir, "blobs"),
                    ["StorageNode:ManifestDbPath"] = Path.Combine(_dataDir, "manifest.db"),
                    ["StorageNode:NodeIdentityKeyPath"] = Path.Combine(_dataDir, "node-identity.pem"),
                });
            });
        }

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
