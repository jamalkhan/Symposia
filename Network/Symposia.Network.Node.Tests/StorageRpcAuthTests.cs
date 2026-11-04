namespace Symposia.Network.Node.Tests;

/// <summary>Issue #140: storage RPC tokens and the registered-caller check (QA TC-1–TC-9).</summary>
public sealed class StorageRpcAuthTests : IDisposable
{
    private const string Method = "/symposia.blobstorage.v1.StorageNode/WriteBlob";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym140-" + Guid.NewGuid().ToString("N"));
    private readonly NodeIdentity _gateway;
    private readonly NodeIdentity _storage;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public StorageRpcAuthTests()
    {
        Directory.CreateDirectory(_dir);
        _gateway = TestSupport.NewIdentity(_dir);
        _storage = TestSupport.NewIdentity(_dir);
    }

    private string? Validate(StorageRpcToken? token, string? self = null, string method = Method, ulong chainId = TestSupport.ChainId,
        string registry = TestSupport.Registry) =>
        StorageRpcAuth.Validate(token, chainId, registry, self ?? _storage.NodeId, method, _now);

    private StorageRpcToken Token(long? at = null, string? audience = null, string method = Method) =>
        _gateway.SignStorageRpc(audience ?? _storage.NodeId, method, at ?? _now.ToUnixTimeSeconds());

    [Fact]
    public void FreshTokenForThisNode_IsValid() => Assert.Null(Validate(Token()));

    [Fact]
    public void MissingToken_IsRejected() => Assert.NotNull(Validate(null));

    [Theory]
    [InlineData(-61)]
    [InlineData(61)]
    public void StaleOrFutureToken_IsRejected(int offsetSeconds) =>
        Assert.Contains("timestamp", Validate(Token(_now.ToUnixTimeSeconds() + offsetSeconds)));

    [Fact]
    public void TokenForAnotherNode_IsRejected() =>
        Assert.NotNull(Validate(Token(audience: _gateway.NodeId)));

    [Fact]
    public void TokenFromAnotherNetwork_IsRejected()
    {
        Assert.NotNull(Validate(Token(), chainId: 1));
        Assert.NotNull(Validate(Token(), registry: "0x0000000000000000000000000000000000000001"));
    }

    [Fact]
    public void TokenForAnotherMethod_IsRejected() =>
        Assert.NotNull(Validate(Token(method: "/symposia.blobstorage.v1.StorageNode/ReadBlob"), method: "/symposia.blobstorage.v1.StorageNode/DeleteBlob"));

    [Fact]
    public void TokenClaimingSomeoneElsesAddress_IsRejected()
    {
        var token = Token() with { Caller = _storage.NodeId };
        Assert.NotNull(Validate(token));
    }

    [Fact]
    public void MalformedSignature_IsRejected() => Assert.NotNull(Validate(Token() with { Signature = "0x1234" }));

    [Fact]
    public async Task CallerRegistry_AcceptsOnlyRegisteredCallers()
    {
        var registry = new FakeRegistry();
        registry.Members[_gateway.NodeId] = "http://gw";
        var callers = new StorageRpcCallerRegistry(registry);

        Assert.True(await callers.IsRegisteredAsync(_gateway.NodeId, CancellationToken.None));
        Assert.False(await callers.IsRegisteredAsync(_storage.NodeId, CancellationToken.None));
    }

    [Fact]
    public async Task CallerRegistry_UsesCacheWhenRegistryIsDown_AndRefusesWithoutOne()
    {
        var registry = new FakeRegistry();
        registry.Members[_gateway.NodeId] = "http://gw";
        var callers = new StorageRpcCallerRegistry(registry);
        Assert.True(await callers.IsRegisteredAsync(_gateway.NodeId, CancellationToken.None));

        registry.Down = true;

        Assert.True(await callers.IsRegisteredAsync(_gateway.NodeId, CancellationToken.None));
        Assert.False(await callers.IsRegisteredAsync("0x00000000000000000000000000000000000000aa", CancellationToken.None));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
