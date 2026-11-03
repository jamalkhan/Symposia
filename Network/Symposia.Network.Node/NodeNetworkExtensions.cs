using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Util;

namespace Symposia.Network.Node;

/// <summary>Wires node identity, registration, discovery, and the handshake endpoints into a node host (issue #129).</summary>
public static class NodeNetworkExtensions
{
    private const string HandshakeRateLimitPolicy = "peer-handshake";

    /// <summary>
    /// Registers node networking services, binding <see cref="NodeNetworkOptions"/>
    /// from the hosting node's own config section.
    /// </summary>
    public static IServiceCollection AddNodeNetwork(this IServiceCollection services, IConfigurationSection section)
    {
        services.Configure<NodeNetworkOptions>(section);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<NodeIdentity>();
        services.AddSingleton<PeerTable>();
        services.AddSingleton<PeerVerifier>();
        services.AddSingleton<PeerDiscoveryService>();

        services.AddHttpClient<INodeRegistryClient, NodeRegistryClient>((sp, http) =>
        {
            var gatewayUrl = sp.GetRequiredService<IOptions<NodeNetworkOptions>>().Value.BlockchainGatewayUrl;
            if (!string.IsNullOrWhiteSpace(gatewayUrl))
            {
                http.BaseAddress = new Uri(gatewayUrl);
            }
        });
        services.AddHttpClient<IPeerHandshakeClient, PeerHandshakeClient>();

        services.AddHostedService<NodeRegistrationHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<PeerDiscoveryService>());

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(HandshakeRateLimitPolicy, http =>
            {
                var perMinute = http.RequestServices.GetRequiredService<IOptions<NodeNetworkOptions>>().Value.HandshakeRateLimitPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });
            });
        });

        return services;
    }

    /// <summary>
    /// Loads (or first-launch generates) the node identity — failing startup
    /// rather than silently re-keying an initialized node — and maps the
    /// peer handshake and peer status endpoints.
    /// </summary>
    public static WebApplication UseNodeNetwork(this WebApplication app)
    {
        app.Services.GetRequiredService<NodeIdentity>().EnsureLoadedOrGenerated();
        app.UseRateLimiter();

        app.MapPost("/v1/peer/handshake", (HandshakeRequest req, NodeIdentity identity) =>
        {
            if (!AddressUtil.Current.IsValidEthereumAddressHexFormat(req.Dialer))
            {
                return Results.BadRequest(new { error = "dialer must be a 0x-prefixed address" });
            }

            byte[] nonce;
            try
            {
                nonce = req.Nonce.HexToByteArray();
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { error = "nonce must be hex" });
            }

            if (nonce.Length != PeerHandshake.NonceLength)
            {
                return Results.BadRequest(new { error = $"nonce must be {PeerHandshake.NonceLength} bytes" });
            }

            return Results.Ok(new HandshakeResponse(identity.NodeId, identity.SignHandshake(req.Dialer, nonce)));
        }).RequireRateLimiting(HandshakeRateLimitPolicy);

        app.MapGet("/v1/peers", (NodeIdentity identity, PeerTable table) => Results.Ok(new
        {
            node = identity.NodeId,
            peers = table.Snapshot().Select(p => new
            {
                endpoint = p.Endpoint,
                address = p.Address,
                status = p.Status.ToString(),
                reason = p.Reason.ToString(),
                lastAttempt = p.LastAttempt,
            }),
        }));

        return app;
    }
}
