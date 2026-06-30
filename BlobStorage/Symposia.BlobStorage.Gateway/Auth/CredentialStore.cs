using Microsoft.Extensions.Options;

namespace Symposia.BlobStorage.Gateway.Auth;

public sealed class CredentialStore
{
    private readonly Dictionary<string, CredentialRecord> _map;

    public CredentialStore(IOptions<GatewayOptions> options)
    {
        _map = options.Value.Credentials.ToDictionary(c => c.AccessKeyId);
    }

    public CredentialRecord? Get(string accessKeyId) =>
        _map.TryGetValue(accessKeyId, out var c) ? c : null;
}
