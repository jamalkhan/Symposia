using Microsoft.Extensions.Options;
using Nethereum.Signer;

namespace Symposia.Network.Node.Tests;

internal static class TestSupport
{
    public const string Registry = "0x5FbDB2315678afecb367f032d93F642f64180aa3";
    public const ulong ChainId = 31337;

    public static NodeNetworkOptions Options(string keyPath) => new()
    {
        NodeIdentityKeyPath = keyPath,
        NodeRegistryAddress = Registry,
        ChainId = ChainId,
        BlockchainGatewayUrl = "http://gateway.local",
        HandshakeTimeoutSeconds = 2,
    };

    public static NodeIdentity NewIdentity(string dir)
    {
        var identity = new NodeIdentity(Microsoft.Extensions.Options.Options.Create(Options(Path.Combine(dir, Guid.NewGuid() + ".key"))));
        identity.EnsureLoadedOrGenerated();
        return identity;
    }
}

internal sealed class FakeRegistry : INodeRegistryClient
{
    public Dictionary<string, string> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ulong> Nonces { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Down { get; set; }
    public List<(string Address, string Endpoint, ulong Nonce)> Registrations { get; } = [];

    public Task<NodeRegistryStatus> GetStatusAsync(string address, CancellationToken ct = default)
    {
        ThrowIfDown();
        var registered = Members.TryGetValue(address, out var endpoint);
        return Task.FromResult(new NodeRegistryStatus(registered, endpoint ?? "", Nonces.GetValueOrDefault(address)));
    }

    public Task<IReadOnlyList<RegisteredNode>> ListNodesAsync(CancellationToken ct = default)
    {
        ThrowIfDown();
        return Task.FromResult<IReadOnlyList<RegisteredNode>>(Members.Select(m => new RegisteredNode(m.Key, m.Value)).ToList());
    }

    public Task RegisterAsync(string address, string endpoint, ulong nonce, byte[] signature, CancellationToken ct = default)
    {
        ThrowIfDown();
        Registrations.Add((address, endpoint, nonce));
        Members[address] = endpoint;
        Nonces[address] = nonce + 1;
        return Task.CompletedTask;
    }

    private void ThrowIfDown()
    {
        if (Down)
        {
            throw new HttpRequestException("gateway down");
        }
    }
}

/// <summary>Routes handshake challenges to in-process responders keyed by endpoint.</summary>
internal sealed class FakeHandshakeNetwork : IPeerHandshakeClient
{
    public Dictionary<string, Func<HandshakeRequest, HandshakeResponse>> Responders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Host(string endpoint, NodeIdentity identity) =>
        Responders[endpoint] = req => new HandshakeResponse(
            identity.NodeId,
            identity.SignHandshake(req.Dialer, Convert.FromHexString(req.Nonce[2..])));

    public Task<HandshakeResponse> ChallengeAsync(string endpoint, HandshakeRequest request, CancellationToken ct = default) =>
        Responders.TryGetValue(endpoint, out var respond)
            ? Task.FromResult(respond(request))
            : throw new HttpRequestException("connection refused");
}
