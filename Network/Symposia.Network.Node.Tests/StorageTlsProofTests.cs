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

    private string Hex => Convert.ToHexStringLower(_fingerprint);

    [Fact]
    public void Proof_VerifiesForTheNodeOnItsNetwork() =>
        Assert.True(StorageTlsProof.Verify(TestSupport.ChainId, TestSupport.Registry, _node.NodeId, Hex, _node.SignStorageTls(_fingerprint)));

    [Fact]
    public void Proof_FailsForAnotherNodeAnotherNetworkOrAnotherCertificate()
    {
        var proof = _node.SignStorageTls(_fingerprint);
        var other = TestSupport.NewIdentity(_dir);
        Assert.False(StorageTlsProof.Verify(TestSupport.ChainId, TestSupport.Registry, other.NodeId, Hex, proof));
        Assert.False(StorageTlsProof.Verify(1, TestSupport.Registry, _node.NodeId, Hex, proof));
        Assert.False(StorageTlsProof.Verify(TestSupport.ChainId, "0x0000000000000000000000000000000000000001", _node.NodeId, Hex, proof));
        Assert.False(StorageTlsProof.Verify(TestSupport.ChainId, TestSupport.Registry, _node.NodeId,
            Convert.ToHexStringLower(SHA256.HashData("other"u8)), proof));
    }

    [Fact]
    public void MissingOrMalformedProof_Fails()
    {
        Assert.False(StorageTlsProof.Verify(TestSupport.ChainId, TestSupport.Registry, _node.NodeId, Hex, null));
        Assert.False(StorageTlsProof.Verify(TestSupport.ChainId, TestSupport.Registry, _node.NodeId, Hex, "0x1234"));
        Assert.False(StorageTlsProof.Verify(TestSupport.ChainId, TestSupport.Registry, _node.NodeId, "abcd", _node.SignStorageTls(_fingerprint)));
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
