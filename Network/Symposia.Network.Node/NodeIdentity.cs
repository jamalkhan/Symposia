using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nethereum.Signer;

namespace Symposia.Network.Node;

/// <summary>
/// Persistent cryptographic node identity, generated at first launch and reloaded thereafter.
/// See Requirements/Network/node-architecture-and-storage.md#node-identity.
/// Uses a secp256k1 keypair (the same curve/address format as #21/#22 and this
/// platform's EVM-compatible chains) so the node's public address is both its
/// peer ID and its on-chain NodeRegistry key with no translation step (#62 FR14).
/// Shared by every node type (issue #129). The private key is generated and
/// stored locally by this process only — no backend/service ever holds it.
/// </summary>
public sealed class NodeIdentity
{
    private readonly NodeNetworkOptions _options;
    private readonly ILogger<NodeIdentity> _logger;
    private EthECKey? _key;

    public NodeIdentity(IOptions<NodeNetworkOptions> options, ILogger<NodeIdentity>? logger = null)
    {
        _options = options.Value;
        _logger = logger ?? NullLogger<NodeIdentity>.Instance;
    }

    /// <summary>The node's EVM-compatible public address, used as its on-chain and network identifier.</summary>
    public string NodeId => Key.GetPublicAddress();

    /// <summary>
    /// Loads the persisted key, or generates one on first launch. A node that
    /// was initialized before (marker file present) but can no longer load its
    /// key fails with <see cref="NodeIdentityException"/> unless
    /// <see cref="NodeNetworkOptions.ResetIdentity"/> is set (#129 FR-A3/FR-A4).
    /// </summary>
    public void EnsureLoadedOrGenerated()
    {
        var keyPath = Path.GetFullPath(_options.NodeIdentityKeyPath);
        var markerPath = keyPath + ".initialized";
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

        var loadError = File.Exists(keyPath) ? TryLoad(keyPath) : $"Node identity key file '{keyPath}' is missing.";
        if (loadError is null)
        {
            if (!File.Exists(markerPath))
            {
                // Backfill for identities created before the marker existed.
                WriteOwnerOnly(markerPath, "");
            }
            return;
        }

        if (File.Exists(markerPath) || File.Exists(keyPath))
        {
            if (!_options.ResetIdentity)
            {
                throw new NodeIdentityException(
                    $"{loadError} This node was previously initialized; refusing to generate a new identity. " +
                    $"Restore the key file, or set NodeIdentity re-init ({nameof(NodeNetworkOptions.ResetIdentity)}=true) to deliberately create a new one.");
            }

            _logger.LogWarning("{Reason} Generating a new node identity because {Option} is set.", loadError, nameof(NodeNetworkOptions.ResetIdentity));
        }

        _key = EthECKey.GenerateKey();
        WriteOwnerOnly(keyPath, _key.GetPrivateKey());
        WriteOwnerOnly(markerPath, "");
    }

    /// <summary>
    /// Loads an existing key and never creates one: for processes that sign on behalf of a host
    /// whose identity is already initialized (the blob gateway, #140), where a fresh key would be a
    /// stranger to the registry.
    /// </summary>
    public void LoadExisting()
    {
        var keyPath = Path.GetFullPath(_options.NodeIdentityKeyPath);
        var error = File.Exists(keyPath) ? TryLoad(keyPath) : $"Node identity key file '{keyPath}' is missing.";
        if (error is not null)
        {
            throw new NodeIdentityException(error);
        }
    }

    private string? TryLoad(string keyPath)
    {
        var content = File.ReadAllText(keyPath).Trim();
        if (content.StartsWith("-----BEGIN", StringComparison.Ordinal))
        {
            return $"Node identity key file '{keyPath}' is a legacy PEM key, not a secp256k1 node key; re-init required.";
        }

        try
        {
            var key = new EthECKey(content);
            _ = key.GetPublicAddress();
            _key = key;
            return null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return $"Node identity key file '{keyPath}' is corrupt or unreadable ({ex.GetType().Name}).";
        }
    }

    /// <summary>
    /// Signs the EIP-712 registration payload for this node's own address and
    /// advertised endpoint, proving control of the private key without it
    /// ever leaving this process (issues #109, #129).
    /// </summary>
    public byte[] SignRegistration(string endpoint, ulong nonce, string nodeRegistryAddress, ulong chainId) =>
        Eip712NodeRegistrySigner.SignRegister(Key, endpoint, nonce, nodeRegistryAddress, chainId);

    /// <summary>Answers a peer handshake challenge from <paramref name="dialer"/> (#129 FR-D1).</summary>
    public string SignHandshake(string dialer, byte[] nonce) =>
        PeerHandshake.Sign(Key, _options.ChainId, _options.NodeRegistryAddress, dialer, nonce);

    /// <summary>Proves this node owns the TLS certificate with this SHA-256 fingerprint (#142).</summary>
    public string SignStorageTls(byte[] fingerprint) =>
        StorageTlsProof.Sign(Key, _options.ChainId, _options.NodeRegistryAddress, fingerprint);

    /// <summary>Signs a storage-node RPC call for <paramref name="audience"/> (#140), using this node's network.</summary>
    public StorageRpcToken SignStorageRpc(string audience, string method, long unixSeconds) =>
        StorageRpcAuth.Sign(Key, _options.ChainId, _options.NodeRegistryAddress, audience, method, unixSeconds);

    /// <summary>
    /// Signs an arbitrary digest with this node's key, in the packed r||s||v
    /// layout the chain contracts expect (issue #109, AC7).
    /// </summary>
    public byte[] Sign(byte[] digest) => Eip712NodeRegistrySigner.Sign(Key, digest);

    private EthECKey Key =>
        _key ?? throw new InvalidOperationException($"{nameof(NodeIdentity)} not loaded. Call {nameof(EnsureLoadedOrGenerated)}() first.");

    /// <summary>
    /// Writes via a temp file created owner-only from the start, then renames
    /// it into place, so the key is never briefly world-readable and a crash
    /// mid-write never leaves a truncated key behind.
    /// </summary>
    private static void WriteOwnerOnly(string path, string content)
    {
        var temp = path + ".tmp";
        var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        File.Delete(temp);
        using (var writer = new StreamWriter(temp, System.Text.Encoding.UTF8, fileOptions))
        {
            writer.Write(content);
        }

        File.Move(temp, path, overwrite: true);
    }
}
