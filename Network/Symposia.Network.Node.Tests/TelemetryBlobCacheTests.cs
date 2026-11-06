using Symposia.Network.Node.Telemetry;
using Symposia.Network.Node.Telemetry.Committee;

namespace Symposia.Network.Node.Tests;

/// <summary>#163: the blob cache evicts least-recently-used entries and never leaves torn files.</summary>
public sealed class TelemetryBlobCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "symposia-blobs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static (byte[] Address, byte[] Content) Blob(int i)
    {
        var content = System.Text.Encoding.UTF8.GetBytes($"batch-{i}");
        return (TelemetryBatchCodec.Address(content), content);
    }

    [Fact]
    public async Task EvictsTheLeastRecentlyUsed_NotEverything()
    {
        var store = new CachingTelemetryBlobStore(null, null);
        var hot = Blob(0);
        await store.PutAsync(hot.Address, hot.Content, default);
        for (var i = 1; i <= CachingTelemetryBlobStore.MaxInMemory + 10; i++)
        {
            var b = Blob(i);
            await store.PutAsync(b.Address, b.Content, default);
            Assert.NotNull(await store.GetAsync(hot.Address, default)); // touched every round: stays cached
        }

        Assert.Equal(CachingTelemetryBlobStore.MaxInMemory, store.InMemoryCount);
        Assert.Null(await store.GetAsync(Blob(1).Address, default)); // oldest untouched entry was evicted (no disk, no backing)
    }

    [Fact]
    public async Task CorruptFileOnDisk_IsDeletedAndNotServed()
    {
        var b = Blob(1);
        await new CachingTelemetryBlobStore(_dir, null).PutAsync(b.Address, b.Content, default);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        var path = Directory.GetFiles(_dir).Single();
        await File.WriteAllTextAsync(path, "torn");

        Assert.Null(await new CachingTelemetryBlobStore(_dir, null).GetAsync(b.Address, default));
        Assert.False(File.Exists(path));
    }
}
