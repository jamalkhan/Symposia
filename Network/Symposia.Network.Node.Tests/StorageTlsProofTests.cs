using System.Security.Cryptography;

namespace Symposia.Network.Node.Tests;

/// <summary>Issue #142: a node's TLS certificate fingerprint is bound to its identity by a signature.</summary>
public sealed class StorageTlsProofTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sym142-" + Guid.NewGuid().ToString("N"));
    private readonly NodeIdentity _node;
    private readonly byte[] _fingerprint = SHA256.HashData("certificate"u8);

    public StorageTlsProofTests()
    {
        Directory.CreateDirectory(_dir);
        _node = TestSupport.NewIdentity(_dir);
    }

    private readonly long _notAfter = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();

    private string Hex => Convert.ToHexStringLower(_fingerprint);

    private string Proof() => _node.SignStorageTls(_fingerprint, _notAfter);

    private string? Check(string node, string hex, string? proof, ulong chain = TestSupport.ChainId, string registry = TestSupport.Registry, long? notAfter = null) =>
        StorageTlsProof.Validate(chain, registry, node, hex, notAfter ?? _notAfter, proof, DateTimeOffset.UtcNow);

    [Fact]
    public void Proof_VerifiesForTheNodeOnItsNetwork() => Assert.Null(Check(_node.NodeId, Hex, Proof()));

    [Fact]
    public void Proof_FailsForAnotherNodeAnotherNetworkOrAnotherCertificate()
    {
        var proof = Proof();
        var other = TestSupport.NewIdentity(_dir);
        Assert.NotNull(Check(other.NodeId, Hex, proof));
        Assert.NotNull(Check(_node.NodeId, Hex, proof, chain: 1));
        Assert.NotNull(Check(_node.NodeId, Hex, proof, registry: "0x0000000000000000000000000000000000000001"));
        Assert.NotNull(Check(_node.NodeId, Convert.ToHexStringLower(SHA256.HashData("other"u8)), proof));
        Assert.NotNull(Check(_node.NodeId, Hex, proof, notAfter: _notAfter + 1)); // expiry is covered by the signature
    }

    [Fact]
    public void MissingOrMalformedProof_Fails()
    {
        Assert.Equal("no certificate proof", Check(_node.NodeId, Hex, null));
        Assert.NotNull(Check(_node.NodeId, Hex, "0x1234"));
        Assert.Equal("malformed certificate fingerprint", Check(_node.NodeId, "abcd", Proof()));
    }

    [Fact]
    public void ExpiredProof_IsRejected()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        var proof = _node.SignStorageTls(_fingerprint, past);
        Assert.Contains("expired", Check(_node.NodeId, Hex, proof, notAfter: past));
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
