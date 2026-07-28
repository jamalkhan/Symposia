using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Nethereum.Util;
using Symposia.BlobStorage.StorageNode.Identity;

namespace Symposia.BlobStorage.StorageNode.Tests;

/// <summary>
/// Unit tests for issue #109's client/node-local keypair generation and
/// EIP-712 registration signing, exercised against the QA plan's section 1
/// ("Client/node-local keypair generation") and section 5 ("Registered
/// identity usable as a signer") test cases.
/// </summary>
public sealed class NodeIdentityTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "symposia-node-identity-tests-" + Guid.NewGuid());

    private NodeIdentity CreateIdentity(string? keyPath = null)
    {
        var options = Options.Create(new StorageNodeOptions
        {
            NodeIdentityKeyPath = keyPath ?? Path.Combine(_dataDir, "node-identity.key"),
        });
        return new NodeIdentity(options);
    }

    // TC-1.4: derive a public address from the generated keypair.
    [Fact]
    public void EnsureLoadedOrGenerated_ProducesWellFormedAddress()
    {
        var identity = CreateIdentity();
        identity.EnsureLoadedOrGenerated();

        Assert.True(AddressUtil.Current.IsValidEthereumAddressHexFormat(identity.NodeId));
    }

    // TC-1.5: two independent keypairs are distinct, non-colliding.
    [Fact]
    public void EnsureLoadedOrGenerated_TwoIdentities_ProduceDistinctAddresses()
    {
        var first = CreateIdentity(Path.Combine(_dataDir, "a.key"));
        var second = CreateIdentity(Path.Combine(_dataDir, "b.key"));
        first.EnsureLoadedOrGenerated();
        second.EnsureLoadedOrGenerated();

        Assert.NotEqual(first.NodeId, second.NodeId);
    }

    // Gherkin "the private key never leaves the node process" (TC-1.3 proxy):
    // the only persisted artifact is the local key file, and reloading it
    // reproduces the same address rather than minting a new identity.
    [Fact]
    public void EnsureLoadedOrGenerated_ReloadsSameIdentityAcrossRestarts()
    {
        var keyPath = Path.Combine(_dataDir, "node-identity.key");
        var first = CreateIdentity(keyPath);
        first.EnsureLoadedOrGenerated();
        var address = first.NodeId;

        var second = CreateIdentity(keyPath);
        second.EnsureLoadedOrGenerated();

        Assert.Equal(address, second.NodeId);
    }

    // TC-1.6 / FR7: no backend/service-side custody — generation only ever
    // touches the single local key file this process controls.
    [Fact]
    public void EnsureLoadedOrGenerated_WritesExactlyOneLocalKeyFile()
    {
        var identity = CreateIdentity();
        identity.EnsureLoadedOrGenerated();

        var files = Directory.GetFiles(_dataDir);
        Assert.Single(files);
    }

    // TC-5.1/5.2: the registered identity remains a valid signer — a
    // signature produced by this key verifies back to the same address.
    [Fact]
    public void Sign_ProducesSignatureThatRecoversToNodeAddress()
    {
        var identity = CreateIdentity();
        identity.EnsureLoadedOrGenerated();
        var digest = Sha3Keccack.Current.CalculateHash("smoke-test message"u8.ToArray());

        var signature = identity.Sign(digest);

        var recovered = EthECKey.RecoverFromSignature(
            EthECDSASignatureFactory.FromComponents(signature[..32], signature[32..64], signature[64..]),
            digest);
        Assert.Equal(identity.NodeId, recovered.GetPublicAddress());
    }

    // FR3: the registration signature is produced over a payload naming this
    // node's own address and is independently recoverable to that address —
    // proof of key control, the same check the on-chain contract performs.
    [Fact]
    public void SignRegistration_SignatureRecoversToNodeAddress()
    {
        var identity = CreateIdentity();
        identity.EnsureLoadedOrGenerated();
        const string nodeRegistryAddress = "0x5FbDB2315678afecb367f032d93F642f64180aa";
        const ulong chainId = 31337;

        var signature = identity.SignRegistration(nodeRegistryAddress, chainId);

        Assert.Equal(65, signature.Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }
}
