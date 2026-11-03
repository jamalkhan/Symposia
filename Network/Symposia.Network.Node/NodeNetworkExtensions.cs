using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        services.TryAddSingleton(TimeProvider.System);
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
        // Never follow redirects: a peer's registered endpoint must not be able
        // to bounce this node's request somewhere else.
        services.AddHttpClient<IPeerHandshakeClient, PeerHandshakeClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        services.AddHostedService<NodeRegistrationHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<PeerDiscoveryService>());

        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(HandshakeRateLimitPolicy, http =>
            {
                var perMinute = http.RequestServices.GetRequiredService<IOptions<NodeNetworkOptions>>().Value.HandshakeRateLimitPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });
            });
            limiter.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    /// <summary>
    /// Convenience for node hosts: <see cref="EnsureNodeIdentity"/>, the rate
    /// limiter middleware the handshake policy needs, then
    /// <see cref="MapNodeNetworkEndpoints"/>. Call it before mapping the
    /// host's own endpoints.
    /// </summary>
    public static WebApplication UseNodeNetwork(this WebApplication app)
    {
        app.EnsureNodeIdentity();
        app.UseRateLimiter();
        app.MapNodeNetworkEndpoints();
        return app;
    }

    /// <summary>
    /// Loads (or first-launch generates) the node identity, failing startup
    /// rather than silently re-keying an initialized node (#129 FR-A3).
    /// </summary>
    public static WebApplication EnsureNodeIdentity(this WebApplication app)
    {
        app.Services.GetRequiredService<NodeIdentity>().EnsureLoadedOrGenerated();
        return app;
    }

    /// <summary>Maps the peer handshake (#129 FR-D1) and peer status (FR-D4) endpoints.</summary>
    public static IEndpointRouteBuilder MapNodeNetworkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/peer/handshake", (HandshakeRequest? req, NodeIdentity identity, HttpContext http, ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("Symposia.Network.Node.PeerHandshake");
            if (req is null || string.IsNullOrEmpty(req.Dialer) || !AddressUtil.Current.IsValidEthereumAddressHexFormat(req.Dialer))
            {
                logger.LogDebug("Rejected handshake from {Remote}: missing or invalid dialer address.", http.Connection.RemoteIpAddress);
                return Results.BadRequest(new { error = "dialer must be a 0x-prefixed address" });
            }

            var nonce = TryParseNonce(req.Nonce);
            if (nonce is null)
            {
                logger.LogDebug("Rejected handshake from {Remote} (dialer {Dialer}): malformed nonce.", http.Connection.RemoteIpAddress, req.Dialer);
                return Results.BadRequest(new { error = $"nonce must be {PeerHandshake.NonceLength} bytes of 0x-prefixed hex" });
            }

            logger.LogTrace("Answering handshake from {Dialer} at {Remote}.", req.Dialer, http.Connection.RemoteIpAddress);

            return Results.Ok(new HandshakeResponse(identity.NodeId, identity.SignHandshake(req.Dialer, nonce)));
        }).RequireRateLimiting(HandshakeRateLimitPolicy);

        app.MapGet("/v1/peers", (NodeIdentity identity, PeerTable table) => Results.Ok(new
        {
            node = identity.NodeId,
            peers = table.Snapshot().Select(p => new
            {
                endpoint = p.Endpoint,
                expectedAddress = p.ExpectedAddress,
                address = p.Address,
                status = p.Status.ToString(),
                reason = p.Reason.ToString(),
                lastAttempt = p.LastAttempt,
            }),
        }));

        return app;
    }

    private static byte[]? TryParseNonce(string? hex)
    {
        if (hex is null || !hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            hex.Length != 2 + PeerHandshake.NonceLength * 2)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(hex.AsSpan(2));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
