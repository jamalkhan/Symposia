using Microsoft.Extensions.Options;
using Nethereum.Signer;

namespace Symposia.BlobStorage.StorageNode.Identity;

/// <summary>
/// Persistent cryptographic node identity, generated at first launch and reloaded thereafter.
/// See Requirements/Network/node-architecture-and-storage.md#node-identity.
/// Uses a secp256k1 keypair (the same curve/address format as #21/#22 and this
/// platform's EVM-compatible chains) so the node's public address can be
/// registered directly against the on-chain NodeRegistry contract (issue #109)
/// with no key translation step. The private key is generated and stored
/// locally by this process only — no backend/service ever holds it.
/// </summary>
public sealed class NodeIdentity
{
    private readonly StorageNodeOptions _options;
    private EthECKey? _key;

    public NodeIdentity(IOptions<StorageNodeOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>The node's EVM-compatible public address, used as its on-chain and network identifier.</summary>
    public string NodeId =>
        _key?.GetPublicAddress() ?? throw new InvalidOperationException($"{nameof(NodeIdentity)} not loaded. Call {nameof(EnsureLoadedOrGenerated)}() first.");

    public void EnsureLoadedOrGenerated()
    {
        var keyPath = Path.GetFullPath(_options.NodeIdentityKeyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

        if (File.Exists(keyPath))
        {
            _key = new EthECKey(File.ReadAllText(keyPath).Trim());
        }
        else
        {
            _key = EthECKey.GenerateKey();
            File.WriteAllText(keyPath, _key.GetPrivateKey());
            RestrictPermissionsToOwner(keyPath);
        }
    }

    /// <summary>
    /// Signs the EIP-712 registration payload for this node's own address,
    /// proving control of the private key without it ever leaving this
    /// process (issue #109, Functional Requirement 3/7).
    /// </summary>
    public byte[] SignRegistration(string nodeRegistryAddress, ulong chainId) =>
        Eip712NodeRegistrySigner.SignRegister(Key, nodeRegistryAddress, chainId);

    /// <summary>
    /// Signs an arbitrary digest with this node's key — smoke-tests that the
    /// registered identity remains usable as a signer for later on-chain
    /// messages (issue #109, AC7), without this class needing to know what
    /// those later messages contain.
    /// </summary>
    public byte[] Sign(byte[] digest) => Eip712NodeRegistrySigner.Sign(Key, digest);

    private EthECKey Key =>
        _key ?? throw new InvalidOperationException($"{nameof(NodeIdentity)} not loaded. Call {nameof(EnsureLoadedOrGenerated)}() first.");

    private static void RestrictPermissionsToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
