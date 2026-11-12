using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.S3;
using Symposia.BlobStorage.Protocol;
using Symposia.Network.Node;
using Symposia.Network.Node.Rewards;

namespace Symposia.BlobStorage.Gateway.Rewards;

/// <summary>
/// The public surfaces the Verifier measures a gateway through (#225 FR3), exempt from SigV4 like every /healthz path
/// and never recorded in operation history:
/// <list type="bullet">
/// <item><see cref="ProbeChallenge.Path"/>: reads the canary through the storage read path and answers the
/// challenge. A request signed by <c>reward.metric.signer</c> gets a reserved lane; everyone else shares a per-IP
/// token bucket and a global concurrency cap, since each call costs a storage-node read.</item>
/// <item><see cref="EpochReportSignature.PathPrefix"/>{epoch}: this gateway's own bytes-served figure, signed by its node key.</item>
/// </list>
/// </summary>
public static class ProbeEndpoints
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    public static void Map(WebApplication app, IReadOnlyList<IPNetwork> trustedProxies)
    {
        app.MapGet(ProbeChallenge.Path, async (HttpContext ctx, ProbeLimiter limits, IServiceProvider services, CancellationToken ct) =>
        {
            if (services.GetService<NodeIdentity>() is not { } identity)
            {
                return Results.NotFound();
            }

            if (!ProbeChallenge.TryParse(ctx.Request.Headers[ProbeChallenge.Header], out var ts, out var nonce, out var signature))
            {
                return Results.BadRequest($"Send {ProbeChallenge.Header}: <unix seconds>.<nonce hex>.<signature hex>.");
            }

            var signer = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) <= ProbeChallenge.MaxSkew.TotalSeconds
                ? ProbeChallenge.Recover(identity.NodeId, ts, nonce, signature) : "";
            using var lease = await limits.AcquireAsync(signer, ClientIp(ctx, trustedProxies), ct);
            if (!lease.IsAcquired)
            {
                ctx.Response.Headers.RetryAfter = "10";
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var canary = await ReadCanaryAsync(services, identity.NodeId, ct);
            return canary is null
                ? Results.Json(new { error = "The canary couldn't be read from storage." }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(new ProbeChallenge.Response(ProbeChallenge.Answer(nonce, canary)));
        });

        app.MapGet(EpochReportSignature.PathPrefix + "{epoch}", async (ulong epoch, HttpContext ctx, ProbeLimiter limits, IServiceProvider services, CancellationToken ct) =>
        {
            if (!limits.AllowReport(ClientIp(ctx, trustedProxies)))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            if (services.GetService<NodeIdentity>() is not { } identity || services.GetService<IServiceMetricsChain>() is not { } chain)
            {
                return Results.NotFound();
            }

            var distributor = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceMetricsOptions>>().Value.RewardDistributorAddress;
            if (services.GetRequiredService<History.HistoryStore>().GetMeasurement(distributor, epoch) is not { } m)
            {
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.NotFound();
            }

            ctx.Response.Headers.CacheControl = "public, max-age=3600"; // a measurement never changes
            return Results.Ok(identity.SignEpochReport(await chain.ChainIdAsync(ct), distributor, epoch, m.Factors.BytesServed, m.Factors.Requests));
        });
    }

    /// <summary>The canary's bytes, read and CID-verified from a replica; null when no replica can serve it.</summary>
    internal static async Task<byte[]?> ReadCanaryAsync(IServiceProvider services, string node, CancellationToken ct)
    {
        var store = services.GetRequiredService<GatewayMetadataStore>();
        if (store.GetObject(ProbeChallenge.CanaryBucket, ProbeChallenge.CanaryKey(node)) is not { } record)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Symposia.BlobStorage.Gateway.Probe");
        try
        {
            var opened = await ReplicaReader.OpenAsync(ReplicaReader.Order(services.GetRequiredService<INodeRegistry>(), record.NodeIds),
                new ReadBlobRequest { Cid = record.Cid }, logger, timeout.Token);
            if (opened?.Call is not { } call)
            {
                return null;
            }

            using (call)
            {
                using var buffer = new MemoryStream();
                var more = opened.HasFirst;
                while (more && buffer.Length <= ProbeChallenge.CanaryBytes)
                {
                    buffer.Write(call.ResponseStream.Current.Data.Span);
                    more = await call.ResponseStream.MoveNext(timeout.Token);
                }

                var bytes = buffer.ToArray();
                return Convert.ToHexStringLower(SHA256.HashData(bytes)) == record.Cid ? bytes : null;
            }
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Probe: canary read failed: {Reason}", ex.Message);
            return null;
        }
    }

    private static string ClientIp(HttpContext ctx, IReadOnlyList<IPNetwork> trustedProxies) =>
        ClientAddress.Resolve(ctx.Connection.RemoteIpAddress, ctx.Request.Headers["X-Forwarded-For"].ToString(), trustedProxies)?.ToString() ?? "unknown";
}

/// <summary>
/// The probe endpoints' limits (#225 arch §5): the metric signer's lane (60 a minute, 2 concurrent) is separate from
/// everyone else's (6 a minute per IP, 2 concurrent overall with a queue of 4), so a flood can't 429 the Verifier.
/// </summary>
public sealed class ProbeLimiter(IServiceMetricsChain? chain, ILogger<ProbeLimiter> logger) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _perIp = PartitionedRateLimiter.Create<string, string>(ip =>
        RateLimitPartition.GetTokenBucketLimiter(ip, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 6, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromSeconds(10), QueueLimit = 0, AutoReplenishment = true,
        }));

    private readonly ConcurrencyLimiter _shared = new(new ConcurrencyLimiterOptions { PermitLimit = 2, QueueLimit = 4, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });

    private readonly FixedWindowRateLimiter _signerRate = new(new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });

    private readonly ConcurrencyLimiter _signerSlots = new(new ConcurrencyLimiterOptions { PermitLimit = 2, QueueLimit = 8, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });

    private readonly PartitionedRateLimiter<string> _reports = PartitionedRateLimiter.Create<string, string>(ip =>
        RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));

    private (string Signer, DateTimeOffset At)? _metricSigner;

    /// <summary>A lease from the signer's lane when <paramref name="signer"/> is the on-chain metric signer, else the shared lane.</summary>
    public async ValueTask<RateLimitLease> AcquireAsync(string signer, string ip, CancellationToken ct)
    {
        if (signer.Length > 0 && string.Equals(signer, await MetricSignerAsync(ct), StringComparison.OrdinalIgnoreCase))
        {
            using var rate = _signerRate.AttemptAcquire();
            return rate.IsAcquired ? await _signerSlots.AcquireAsync(1, ct) : rate;
        }

        using var perIp = _perIp.AttemptAcquire(ip);
        return perIp.IsAcquired ? await _shared.AcquireAsync(1, ct) : perIp;
    }

    public bool AllowReport(string ip)
    {
        using var lease = _reports.AttemptAcquire(ip);
        return lease.IsAcquired;
    }

    /// <summary>Re-read every 5 minutes; "" when unknown, so an unreachable chain only closes the reserved lane.</summary>
    private async Task<string> MetricSignerAsync(CancellationToken ct)
    {
        if (chain is null)
        {
            return "";
        }

        if (_metricSigner is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(5))
        {
            return cached.Signer;
        }

        try
        {
            var signer = await chain.MetricSignerAsync(ct);
            _metricSigner = (signer, DateTimeOffset.UtcNow);
            return signer;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Probe: couldn't read the metric signer; the reserved lane is closed until it can: {Reason}", ex.Message);
            _metricSigner = ("", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(4)); // retry in a minute
            return "";
        }
    }

    public void Dispose()
    {
        _perIp.Dispose();
        _shared.Dispose();
        _signerRate.Dispose();
        _signerSlots.Dispose();
        _reports.Dispose();
    }
}
