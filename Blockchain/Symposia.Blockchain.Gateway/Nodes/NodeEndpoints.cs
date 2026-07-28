using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.JsonRpc.Client;
using Symposia.Blockchain.Gateway.Chain;

namespace Symposia.Blockchain.Gateway.Nodes;

public sealed record RegisterRequest(string Node, string Signature);
public sealed record SubmitEpochRootRequest(ulong Epoch, string Root, string Signature);

/// <summary>
/// Bootstrap Chain Gateway write/read surface (issue #110, Functional
/// Requirement 8 — reachable over a network-addressable RPC/SDK boundary,
/// no manual/human intervention per node or per epoch). Writes relay
/// already-signed payloads on-chain and sponsor gas; reads proxy the
/// contracts' view functions directly.
/// </summary>
public static class NodeEndpoints
{
    public static void MapNodeEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/nodes/register", async (RegisterRequest req, BootstrapChainClient chain) =>
        {
            // Anti-abuse (per the Arch pass): don't relay a transaction the
            // contract would just no-op anyway — the contract's own
            // idempotency (Functional Requirement 10) is the backstop either
            // way, this only saves the relayer's gas.
            if (await chain.IsRegisteredAsync(req.Node))
            {
                return Results.Ok(new { node = req.Node, registered = true, relayed = false });
            }

            try
            {
                var txHash = await chain.RegisterAsync(req.Node, req.Signature.HexToByteArray());
                return Results.Ok(new { node = req.Node, registered = true, relayed = true, txHash });
            }
            catch (RpcResponseException ex)
            {
                // The contract itself is the authoritative rejector for a
                // forged registration signature (Requirement 5's analogue
                // for NodeRegistry) — surface its revert reason as a client error.
                return Results.BadRequest(new { node = req.Node, error = ex.Message });
            }
        });

        app.MapGet("/v1/nodes/{address}", async (string address, BootstrapChainClient chain) =>
        {
            var registered = await chain.IsRegisteredAsync(address);
            return Results.Ok(new { node = address, registered });
        });

        app.MapPost("/v1/nodes/{address}/epoch-roots", async (string address, SubmitEpochRootRequest req, BootstrapChainClient chain) =>
        {
            var rootBytes = req.Root.HexToByteArray();
            var existing = await chain.GetRootAsync(address, req.Epoch);
            var hasExisting = existing.Any(b => b != 0);

            if (hasExisting)
            {
                if (existing.SequenceEqual(rootBytes))
                {
                    return Results.Ok(new { node = address, epoch = req.Epoch, root = req.Root, relayed = false });
                }

                // Same rejection the contract would give, returned without
                // spending relayer gas on a submission guaranteed to revert.
                return Results.Conflict(new
                {
                    error = "conflicting resubmission for epoch",
                    node = address,
                    epoch = req.Epoch,
                });
            }

            try
            {
                var txHash = await chain.SubmitRootAsync(address, req.Epoch, rootBytes, req.Signature.HexToByteArray());
                return Results.Ok(new { node = address, epoch = req.Epoch, root = req.Root, relayed = true, txHash });
            }
            catch (RpcResponseException ex)
            {
                // Covers both rejection paths the contract enforces:
                // unregistered node (Requirement 4) and forged/tampered
                // signature (Requirement 5).
                return Results.BadRequest(new { node = address, epoch = req.Epoch, error = ex.Message });
            }
        });

        app.MapGet("/v1/nodes/{address}/epoch-roots/latest", async (string address, BootstrapChainClient chain) =>
        {
            var latest = await chain.TryGetLatestRootAsync(address);
            return latest is null
                ? Results.NotFound(new { node = address, error = "no submissions for node" })
                : Results.Ok(new { node = address, epoch = latest.Value.Epoch, root = latest.Value.Root.ToHex(prefix: true) });
        });

        app.MapGet("/v1/nodes/{address}/epoch-roots/{epoch}", async (string address, ulong epoch, BootstrapChainClient chain) =>
        {
            var root = await chain.GetRootAsync(address, epoch);
            return root.All(b => b == 0)
                ? Results.NotFound(new { node = address, epoch, error = "no root recorded for epoch" })
                : Results.Ok(new { node = address, epoch, root = root.ToHex(prefix: true) });
        });
    }
}
