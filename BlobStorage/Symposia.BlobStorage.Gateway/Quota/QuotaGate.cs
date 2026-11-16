using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.S3;

namespace Symposia.BlobStorage.Gateway.Quota;

/// <summary>A 507 for a write over quota (#15 FR3, AC1), with the error-catalog <c>details</c>.</summary>
public sealed class QuotaExceededException : S3RequestRejectedException
{
    public QuotaExceededException(QuotaRefusal refusal, bool freeTier)
        : base(StatusCodes.Status507InsufficientStorage, "QuotaExceeded", Describe(refusal, freeTier))
    {
        Refusal = refusal;
        NativeCode = refusal.QuotaType == "account" ? "AccountQuotaExceeded" : "BucketQuotaExceeded";
        Details = refusal.Unit == "bytes"
            ? new Dictionary<string, object?>
            {
                ["quota_type"] = refusal.QuotaType,
                ["limit_bytes"] = refusal.Limit,
                ["used_bytes"] = refusal.Used,
                ["incoming_bytes"] = refusal.Incoming,
                ["exceeded_by_bytes"] = Math.Max(0, refusal.Used + refusal.Incoming - refusal.Limit),
                ["payment_required"] = freeTier ? true : null,
            }
            : new Dictionary<string, object?>
            {
                ["quota_type"] = refusal.QuotaType,
                ["limit_objects"] = refusal.Limit,
                ["used_objects"] = refusal.Used,
                ["incoming_objects"] = refusal.Incoming,
            };
    }

    public QuotaRefusal Refusal { get; }

    private static string Describe(QuotaRefusal r, bool freeTier) => r.Unit == "bytes"
        ? $"This write of {r.Incoming} bytes would exceed the {r.QuotaType} quota of {r.Limit} bytes ({r.Used} in use) by " +
          $"{Math.Max(0, r.Used + r.Incoming - r.Limit)} bytes." +
          (freeTier ? " The free tier is full: add payment credentials to raise this limit." : "")
        : $"This bucket already holds its maximum of {r.Limit} objects.";
}

/// <summary>
/// An in-flight write's hold on quota (#15 FR4). Renewed in the background while the write runs; committing or
/// disposing ends it. Usage itself is charged by the commit's own transaction, so a committed write's reservation
/// is simply dropped, and a failed one's headroom comes back the moment it is disposed.
/// </summary>
public sealed class QuotaReservation : IDisposable
{
    private readonly GatewayMetadataStore? _store;
    private readonly Timer? _renew;
    private int _ended;

    internal QuotaReservation(GatewayMetadataStore store, long id, TimeSpan lease)
    {
        _store = store;
        Id = id;
        var every = lease / 3;
        _renew = new Timer(_ =>
        {
            try
            {
                store.RenewReservation(id, lease);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // A busy index: the next tick retries, well inside the lease.
            }
        }, null, every, every);
    }

    private QuotaReservation()
    {
    }

    /// <summary>A write that needs no reservation (no quota applies).</summary>
    public static QuotaReservation None { get; } = new();

    public long? Id { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0 && Id is { } id)
        {
            _renew!.Dispose();
            _store!.EndReservation(id);
        }
    }
}

/// <summary>
/// Quota enforcement (#15, Requirements/BlobStorage/quota-management.md). Every write reserves its size against the
/// account's effective limit (the lower of the platform-imposed limit and the tenant's self-limit) and the bucket's
/// byte and object-count quotas before placement or any node contact, so an over-quota upload costs nothing and two
/// racing writes can't jointly overrun a limit (AC3). Accounts are provisioned on first use with the free tier (FR8).
/// </summary>
/// <remarks>
/// The reservation is atomic in this gateway's metadata database. Two gateways over separate databases keep separate
/// counters, so cross-gateway race safety (QA-17) needs the shared metadata index tracked as a follow-up to #12.
/// A body whose length isn't declared reserves nothing beyond "the quota isn't full": it can overshoot by its own size.
/// </remarks>
public sealed class QuotaGate(GatewayMetadataStore store, IOptions<GatewayOptions> options, IQuotaAlertSink alerts, ILogger<QuotaGate> logger)
{
    private GatewayOptions Options => options.Value;

    /// <summary>What a tenant's account starts with: an operator override, else the free tier.</summary>
    public AccountDefaults DefaultsFor(string tenantId) =>
        Options.TenantQuotaBytes.TryGetValue(tenantId, out var configured)
            ? new AccountDefaults(configured, null, FreeTier: false)
            : Options.Quota.FreeTierStorageBytes > 0
                ? new AccountDefaults(Options.Quota.FreeTierStorageBytes, Options.Quota.FreeTierEgressBytesPerMonth, FreeTier: true)
                : new AccountDefaults(null, null, FreeTier: false);

    /// <summary>The platform-imposed limit: the operator's configured override when there is one, else the account's own.</summary>
    public long? PlatformLimit(AccountQuota account) =>
        Options.TenantQuotaBytes.TryGetValue(account.TenantId, out var configured) ? configured : account.PlatformLimitBytes;

    /// <summary>min(platform-imposed, self-limit), null = unlimited (#15 FR1, AC7).</summary>
    public long? EffectiveLimit(AccountQuota account) => (PlatformLimit(account), account.SelfLimitBytes) switch
    {
        ({ } p, { } s) => Math.Min(p, s),
        (var p, var s) => p ?? s,
    };

    public AccountQuota Account(string tenantId) => store.Account(tenantId, DefaultsFor(tenantId));

    /// <summary>Reserves a write of <paramref name="incomingBytes"/> (null = unknown), or throws <see cref="QuotaExceededException"/>.</summary>
    public QuotaReservation Reserve(string tenantId, string bucket, string key, long? incomingBytes)
    {
        var lease = TimeSpan.FromSeconds(Math.Max(3, Options.Quota.ReservationLeaseSeconds));
        AccountQuota? seen = null;
        var (id, refusal) = store.TryReserve(tenantId, DefaultsFor(tenantId), a => EffectiveLimit(seen = a), bucket, key, Math.Max(0, incomingBytes ?? 0), lease);
        if (refusal is not null)
        {
            logger.LogInformation("Refused a {Size}-byte write to {Bucket}/{Key} by {Tenant}: {Type} {Unit} quota {Limit} ({Used} in use).",
                incomingBytes, bucket, key, tenantId, refusal.QuotaType, refusal.Unit, refusal.Limit, refusal.Used);
            throw new QuotaExceededException(refusal, refusal.QuotaType == "account" && seen?.FreeTier == true && PlatformLimit(seen) == EffectiveLimit(seen));
        }

        return new QuotaReservation(store, id!.Value, lease);
    }

    /// <summary>
    /// A multipart part (#15 AC6): parts don't count toward quota until the upload completes, so only an account
    /// that is already full refuses them; there'd be nothing for the completed object to fit into.
    /// </summary>
    public void EnsureNotFull(string tenantId)
    {
        var account = Account(tenantId);
        if (EffectiveLimit(account) is { } limit && store.AccountUsage(tenantId) is var usage && usage.UsedBytes + usage.ReservedBytes >= limit)
        {
            throw new QuotaExceededException(new QuotaRefusal("account", "bytes", limit, usage.UsedBytes + usage.ReservedBytes, 0),
                account.FreeTier && PlatformLimit(account) == limit);
        }
    }

    /// <summary>
    /// After a committed write: alerts for each threshold the account or bucket newly crossed (FR7, AC10). Never fails
    /// or delays the write; a broken alert channel is logged and skipped.
    /// </summary>
    public void AfterCommit(string tenantId, string bucket)
    {
        try
        {
            var thresholds = Options.Quota.AlertThresholdsPct.Where(t => t > 0).Distinct().Order().ToList();
            if (thresholds.Count == 0)
            {
                return;
            }

            if (EffectiveLimit(Account(tenantId)) is { } accountLimit)
            {
                Alert("account", tenantId, tenantId, null, store.AccountUsage(tenantId).UsedBytes, accountLimit, thresholds);
            }

            if (store.GetBucketQuota(bucket)?.LimitBytes is { } bucketLimit)
            {
                Alert("bucket", bucket, tenantId, bucket, store.BucketUsage(bucket).UsedBytes, bucketLimit, thresholds);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Quota alert evaluation failed for {Tenant}/{Bucket}; the write is unaffected.", tenantId, bucket);
        }
    }

    private void Alert(string scope, string scopeId, string tenantId, string? bucket, long used, long limit, IReadOnlyList<int> thresholds)
    {
        var utilization = Utilization(used, limit);
        foreach (var threshold in store.CrossThresholds(scope, scopeId, utilization, thresholds))
        {
            alerts.Notify(new QuotaAlert(tenantId, scope, bucket, threshold, Severity(threshold), utilization, used, limit, DateTimeOffset.UtcNow));
        }
    }

    public static double Utilization(long used, long limit) => limit <= 0 ? 100 : Math.Round(100.0 * used / limit, 2);

    /// <summary>INFO below 90 %, WARNING below 100 %, CRITICAL at 100 % and above (the defaults' severities, for any threshold).</summary>
    public static QuotaAlertSeverity Severity(int thresholdPct) => thresholdPct switch
    {
        >= 100 => QuotaAlertSeverity.Critical,
        >= 90 => QuotaAlertSeverity.Warning,
        _ => QuotaAlertSeverity.Info,
    };
}
