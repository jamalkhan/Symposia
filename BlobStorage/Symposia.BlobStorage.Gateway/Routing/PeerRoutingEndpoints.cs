using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;

namespace Symposia.BlobStorage.Gateway.Routing;

/// <summary>
/// Gateway-to-gateway CID resolution (#12 FR2.6, FR4.4): what this gateway knows about who holds a CID, from its
/// routing cache and its own metadata index. Only node IDs cross: no tenant, bucket, key or data. Callers present
/// <see cref="RoutingOptions.PeerToken"/>; with no token configured the endpoint refuses everyone.
/// </summary>
public static class PeerRoutingEndpoints
{
    public const string Path = "/internal/v1/routing";

    public sealed record Answer(string Cid, IReadOnlyList<string> Nodes, bool Warm);

    public static bool IsPeerPath(string path) => path.StartsWith(Path + "/", StringComparison.Ordinal);

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet(Path + "/{cid}", (string cid, HttpContext ctx, RoutingCache cache, GatewayMetadataStore store, IOptions<GatewayOptions> options) =>
        {
            var routing = options.Value.Routing;
            if (!routing.AcceptedPeerTokens.Prepend(routing.PeerToken).Any(t => Authorized(ctx.Request.Headers.Authorization.ToString(), t)))
            {
                return Results.Unauthorized();
            }

            if (!Domain.Cid.TryParse(cid, out _))
            {
                return Results.BadRequest();
            }

            var holders = cache.Holders(cid).Union(store.ReplicasOfCid(cid), StringComparer.OrdinalIgnoreCase).ToList();
            return Results.Ok(new Answer(cid, holders, cache.IsWarm));
        });

    internal static bool Authorized(string header, string token)
    {
        const string Scheme = "Bearer ";
        if (token.Length == 0 || !header.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[Scheme.Length..]), Encoding.UTF8.GetBytes(token));
    }
}
