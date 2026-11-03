using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node.Tests;

/// <summary>
/// Node identity lifecycle (issues #109, #129 FR-A1–FR-A4; #129 QA TC-1–TC-5).
/// </summary>
public sealed class NodeIdentityTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "symposia-network-identity-" + Guid.NewGuid());

    private string KeyPath => Path.Combine(_dataDir, "node-identity.key");

    private NodeIdentity Create(bool reset = false)
    {
        var options = TestSupport.Options(KeyPath);
        options.ResetIdentity = reset;
        return new NodeIdentity(Options.Create(options));
    }

    // TC-1: fresh start creates an owner-only key and the init marker.
    [Fact]
    public void FreshNode_GeneratesKeyAndMarker()
    {
        var identity = Create();
        identity.EnsureLoadedOrGenerated();

        Assert.True(AddressUtil.Current.IsValidEthereumAddressHexFormat(identity.NodeId));
        Assert.True(File.Exists(KeyPath));
        Assert.True(File.Exists(KeyPath + ".initialized"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
        }
    }

    [Fact]
    public void Restart_ReloadsSameIdentity()
    {
        var first = Create();
        first.EnsureLoadedOrGenerated();

        var second = Create();
        second.EnsureLoadedOrGenerated();

        Assert.Equal(first.NodeId, second.NodeId);
    }

    // TC-2 / Gherkin "Missing key after initialization".
    [Fact]
    public void InitializedNode_MissingKey_FailsWithoutGenerating()
    {
        Create().EnsureLoadedOrGenerated();
        File.Delete(KeyPath);

        var ex = Assert.Throws<NodeIdentityException>(() => Create().EnsureLoadedOrGenerated());

        Assert.Contains(KeyPath, ex.Message);
        Assert.False(File.Exists(KeyPath));
    }

    // TC-3.
    [Fact]
    public void InitializedNode_CorruptKey_Fails()
    {
        Create().EnsureLoadedOrGenerated();
        File.WriteAllText(KeyPath, "not-a-key");

        Assert.Throws<NodeIdentityException>(() => Create().EnsureLoadedOrGenerated());
    }

    // FR-A4: legacy P-256 PEM compute-node keys require explicit re-init.
    [Fact]
    public void LegacyPemKey_FailsWithReinitMessage()
    {
        Directory.CreateDirectory(_dataDir);
        using var p256 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        File.WriteAllText(KeyPath, p256.ExportPkcs8PrivateKeyPem());

        var ex = Assert.Throws<NodeIdentityException>(() => Create().EnsureLoadedOrGenerated());

        Assert.Contains("re-init", ex.Message);
    }

    // TC-4.
    [Fact]
    public void ResetIdentity_AfterKeyLoss_GeneratesNewKey()
    {
        var original = Create();
        original.EnsureLoadedOrGenerated();
        File.Delete(KeyPath);

        var reset = Create(reset: true);
        reset.EnsureLoadedOrGenerated();

        Assert.True(File.Exists(KeyPath));
        Assert.NotEqual(original.NodeId, reset.NodeId);
    }

    // ResetIdentity never replaces a valid key.
    [Fact]
    public void ResetIdentity_WithValidKey_KeepsIdentity()
    {
        var original = Create();
        original.EnsureLoadedOrGenerated();

        var again = Create(reset: true);
        again.EnsureLoadedOrGenerated();

        Assert.Equal(original.NodeId, again.NodeId);
    }

    // Identities created before #129 have no marker; it is backfilled on load.
    [Fact]
    public void PreexistingKeyWithoutMarker_LoadsAndBackfillsMarker()
    {
        Directory.CreateDirectory(_dataDir);
        var key = EthECKey.GenerateKey();
        File.WriteAllText(KeyPath, key.GetPrivateKey());

        var identity = Create();
        identity.EnsureLoadedOrGenerated();

        Assert.Equal(key.GetPublicAddress(), identity.NodeId);
        Assert.True(File.Exists(KeyPath + ".initialized"));
    }

    // TC-5: the address is a pure function of the key bytes.
    [Fact]
    public void SameKeyBytes_DeriveSameAddress()
    {
        Directory.CreateDirectory(_dataDir);
        var key = EthECKey.GenerateKey();
        File.WriteAllText(KeyPath, key.GetPrivateKey());

        var identity = Create();
        identity.EnsureLoadedOrGenerated();

        Assert.Equal(key.GetPublicAddress(), identity.NodeId);
    }

    [Fact]
    public void Sign_RecoversToNodeAddress()
    {
        var identity = Create();
        identity.EnsureLoadedOrGenerated();
        var digest = Sha3Keccack.Current.CalculateHash("smoke-test message"u8.ToArray());

        var signature = identity.Sign(digest);

        var recovered = EthECKey.RecoverFromSignature(
            EthECDSASignatureFactory.FromComponents(signature[..32], signature[32..64], signature[64..]), digest);
        Assert.Equal(identity.NodeId, recovered.GetPublicAddress());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }
}

public sealed class Eip712NodeRegistrySignerTests
{
    // Regression: R/S with leading zero bytes must still yield 65-byte signatures.
    [Fact]
    public void Sign_AlwaysProduces65ByteSignatureThatRecovers()
    {
        var key = Nethereum.Signer.EthECKey.GenerateKey();
        for (var i = 0; i < 2000; i++)
        {
            var digest = Nethereum.Util.Sha3Keccack.Current.CalculateHash(BitConverter.GetBytes(i));
            var sig = Eip712NodeRegistrySigner.Sign(key, digest);

            Assert.Equal(65, sig.Length);
            var recovered = Nethereum.Signer.EthECKey.RecoverFromSignature(
                Nethereum.Signer.EthECDSASignatureFactory.FromComponents(sig[..32], sig[32..64], sig[64..]), digest);
            Assert.Equal(key.GetPublicAddress(), recovered.GetPublicAddress());
        }
    }
}
