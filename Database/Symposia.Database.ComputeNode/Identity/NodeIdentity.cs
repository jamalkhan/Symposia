using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Symposia.Database.ComputeNode.Identity;

/// <summary>
/// Persistent cryptographic node identity, generated at first launch and reloaded thereafter.
/// See Requirements/Network/node-architecture-and-storage.md#node-identity.
/// The compute node reuses this identity (rather than a compute-specific PKI) to authenticate
/// both its proxy-facing wire-protocol listener and its peer-safekeeper listener.
/// </summary>
public sealed class NodeIdentity
{
    private readonly ComputeNodeOptions _options;
    private ECDsa? _keyPair;
    private string? _nodeId;

    public NodeIdentity(IOptions<ComputeNodeOptions> options)
    {
        _options = options.Value;
    }

    public string NodeId =>
        _nodeId ?? throw new InvalidOperationException($"{nameof(NodeIdentity)} not loaded. Call {nameof(EnsureLoadedOrGenerated)}() first.");

    public void EnsureLoadedOrGenerated()
    {
        var keyPath = Path.GetFullPath(_options.NodeIdentityKeyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

        var keyPair = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (File.Exists(keyPath))
        {
            keyPair.ImportFromPem(File.ReadAllText(keyPath));
        }
        else
        {
            var pem = keyPair.ExportPkcs8PrivateKeyPem();
            File.WriteAllText(keyPath, pem);
            RestrictPermissionsToOwner(keyPath);
        }

        _keyPair = keyPair;
        _nodeId = Convert.ToHexStringLower(SHA256.HashData(keyPair.ExportSubjectPublicKeyInfo()));
    }

    private static void RestrictPermissionsToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
