using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain.Credentials;
using Symposia.BlobStorage.Protocol.Credentials;

namespace Symposia.BlobStorage.Gateway.Auth;

/// <summary>
/// Keeps <see cref="CredentialStore"/> current with the Credential Authority (#3 FR5.18, #245). The Watch stream
/// pushes each new state version, and a poll every <see cref="CredentialAuthorityOptions.PollSeconds"/> covers a
/// dropped stream; both pull only the <em>changes</em> since the cached version (<c>/internal/changes</c>), so an
/// unchanged credential's secret never crosses the wire again (#245 FR3). The full snapshot is pulled only at
/// startup, when the Authority answers 410 (too far behind, a restored database, an oversized delta), from a
/// pre-#245 Authority (404), and on a jittered reconcile every <see cref="CredentialAuthorityOptions.ReconcileMinutes"/>
/// as a self-healing backstop. Revocation still reaches this gateway within one poll interval (FR5).
/// </summary>
public sealed class CredentialSync(CredentialStore cache, IOptions<GatewayOptions> options, IHttpClientFactory http, ILogger<CredentialSync> logger, IMeterFactory meters)
    : BackgroundService
{
    public const string HttpClientName = "credential-authority";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // From IMeterFactory, so each host (and each test server) gets its own instruments (review: Carl #7).
    private readonly Meter _meter = meters.Create("Symposia.BlobStorage.Gateway.CredentialSync");
    private Counter<long>? _syncs;

    // One sync at a time: a Watch tick arriving mid-sync coalesces into the running one (#245 Arch §1.3).
    private readonly SemaphoreSlim _single = new(1, 1);
    private int _dirty;
    private volatile bool _deltaUnsupported;

    /// <summary>Counts of each kind of sync (delta, snapshot, fallback_410, fallback_404, reconcile), for tests and diagnostics.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, long> Counts { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.CredentialAuthority;
        if (string.IsNullOrWhiteSpace(o.Url))
        {
            return;
        }

        _meter.CreateObservableGauge("credential_state_version", () => cache.Version);
        _meter.CreateObservableGauge("credential_state_seconds_since_applied", () =>
            cache.LastApplied == default ? -1 : (DateTimeOffset.UtcNow - cache.LastApplied).TotalSeconds);
        await SnapshotAsync("snapshot", stoppingToken);
        await Task.WhenAll(WatchLoopAsync(stoppingToken), PollLoopAsync(stoppingToken), ReconcileLoopAsync(stoppingToken));
    }

    /// <summary>Brings the cache up to date: a delta when possible, the snapshot when not. True when state changed.</summary>
    /// <param name="wait">
    /// Wait for a running sync and then sync again, rather than coalescing into it: for callers that must see a
    /// change they just made (the management proxy).
    /// </param>
    public async Task<bool> SyncAsync(CancellationToken ct, bool wait = false)
    {
        if (!await _single.WaitAsync(wait ? Timeout.Infinite : 0, ct))
        {
            // A sync is running. Mark it dirty so it runs once more when done: the change behind this request may
            // have committed after that sync's read, and must not wait a whole poll interval (review: Lenny #9).
            Volatile.Write(ref _dirty, 1);
            return false;
        }

        try
        {
            var applied = false;
            do
            {
                Volatile.Write(ref _dirty, 0);
                applied |= await SyncOnceAsync(ct);
            }
            while (Volatile.Read(ref _dirty) == 1 && !ct.IsCancellationRequested);

            return applied;
        }
        finally
        {
            _single.Release();
        }
    }

    private async Task<bool> SyncOnceAsync(CancellationToken ct)
    {
        try
        {
            if (!cache.Loaded || _deltaUnsupported)
            {
                return await SnapshotCoreAsync("snapshot", ct);
            }

            using var response = await http.CreateClient(HttpClientName).GetAsync($"internal/changes?since={cache.Version}", ct);
            switch ((int)response.StatusCode)
            {
                case 200:
                    var delta = await response.Content.ReadFromJsonAsync<AuthorityDelta>(Json, ct);
                    var result = delta is null ? CredentialStore.DeltaResult.NeedsSnapshot : cache.ApplyDelta(delta);
                    if (result == CredentialStore.DeltaResult.NeedsSnapshot)
                    {
                        return await SnapshotCoreAsync("fallback_410", ct);
                    }

                    Count("delta");
                    if (result == CredentialStore.DeltaResult.Applied)
                    {
                        logger.LogDebug("Credential delta {From} → {To}: {Credentials} credential(s), {Deleted} deleted, {Tenants} tenant(s), {Rules} rule set(s).",
                            delta!.FromVersion, delta.Version, delta.Credentials.Count, delta.DeletedCredentialIds.Count, delta.Tenants.Count, delta.RuleSets.Count);
                    }

                    return result == CredentialStore.DeltaResult.Applied;
                case 410:
                    // AheadOfAuthority: the Authority's version is below ours, so its database went backwards (a
                    // restore without a new epoch). Its state is the truth now; install it whatever its version, or
                    // the cache would freeze until the Authority caught up (review: Lenny #1, Frank #3).
                    var refusal = await ReadRefusalAsync(response, ct);
                    return await SnapshotCoreAsync("fallback_410", ct, force: refusal == "AheadOfAuthority");
                case 404:
                    // An Authority from before #245: snapshot polling until the next reconcile checks again (FR9).
                    _deltaUnsupported = true;
                    return await SnapshotCoreAsync("fallback_404", ct);
                default:
                    logger.LogWarning("Credential delta pull answered {Status}; keeping version {Version}.", (int)response.StatusCode, cache.Version);
                    return false;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Credential Authority sync failed: {Message}. Keeping version {Version}.", ex.Message, cache.Version);
            return false;
        }
    }

    private static async Task<string?> ReadRefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return body.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
        }
        catch (JsonException)
        {
            return null; // an older Authority's 410 carries no reason: an ordinary fallback
        }
    }

    /// <summary>Pulls and applies the full snapshot (startup, reconcile, tests).</summary>
    public async Task<bool> PullAsync(CancellationToken ct) => await SnapshotAsync("snapshot", ct);

    private async Task<bool> SnapshotAsync(string kind, CancellationToken ct)
    {
        await _single.WaitAsync(ct);
        try
        {
            return await SnapshotCoreAsync(kind, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Credential Authority snapshot pull failed: {Message}. Keeping version {Version}.", ex.Message, cache.Version);
            return false;
        }
        finally
        {
            _single.Release();
        }
    }

    private async Task<bool> SnapshotCoreAsync(string kind, CancellationToken ct, bool force = false)
    {
        var snapshot = await http.CreateClient(HttpClientName).GetFromJsonAsync<AuthoritySnapshot>("internal/snapshot", Json, ct);
        Count(kind);
        if (snapshot is not null && cache.Apply(snapshot, force))
        {
            logger.LogDebug("Credential state now at version {Version} (full snapshot, {Kind}).", snapshot.Version, kind);
            return true;
        }

        return false;
    }

    private void Count(string kind)
    {
        Counts.AddOrUpdate(kind, 1, (_, n) => n + 1);
        (_syncs ??= _meter.CreateCounter<long>("credential_sync_total")).Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.CredentialAuthority.PollSeconds)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            await SyncAsync(ct);
        }
    }

    /// <summary>The full-snapshot backstop, jittered ±20 % so a fleet doesn't pull together (#245 FR4c).</summary>
    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        var minutes = Math.Max(0.05, options.Value.CredentialAuthority.ReconcileMinutes);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(minutes * (0.8 + Random.Shared.NextDouble() * 0.4)), ct);
            _deltaUnsupported = false; // an upgraded Authority is noticed at the next reconcile
            await SnapshotAsync("reconcile", ct);
        }
    }

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        var o = options.Value.CredentialAuthority;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var channel = GrpcChannel.ForAddress(o.Url, new GrpcChannelOptions { HttpClient = http.CreateClient(HttpClientName + "-grpc") });
                var client = new Protocol.Credentials.CredentialAuthority.CredentialAuthorityClient(channel);
                using var call = client.Watch(new WatchRequest { SinceVersion = cache.Version },
                    new Grpc.Core.Metadata { { "x-service-token", o.ServiceToken } }, cancellationToken: ct);
                while (await call.ResponseStream.MoveNext(ct))
                {
                    if (call.ResponseStream.Current.Version != cache.Version)
                    {
                        await SyncAsync(ct);
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Credential Watch stream dropped ({Message}); polling covers the gap. Reconnecting.", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}

/// <summary>
/// Sends use events to the Authority in batches, off the request path: a slow or unreachable Authority never
/// delays or fails a request (#3 Arch §4). Undelivered batches are spooled to a local SQLite file and sent first
/// once the Authority is back; past <see cref="CredentialAuthorityOptions.AuditSpoolLimit"/> the oldest are
/// dropped, loudly, rather than growing without bound.
/// </summary>
public sealed class AuditForwarder : BackgroundService
{
    private readonly Channel<UseReport> _queue = Channel.CreateBounded<UseReport>(new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly IOptions<GatewayOptions> _options;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AuditForwarder> _logger;
    private readonly string _spool;

    public AuditForwarder(IOptions<GatewayOptions> options, IHttpClientFactory http, ILogger<AuditForwarder> logger)
    {
        _options = options;
        _http = http;
        _logger = logger;
        _spool = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Value.MetadataDbPath)) ?? ".", "audit-spool.db"),
        }.ToString();
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_options.Value.CredentialAuthority.Url);

    public void Report(UseReport use)
    {
        if (Enabled && !use.CredentialId.StartsWith("static:", StringComparison.Ordinal))
        {
            _queue.Writer.TryWrite(use);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            return;
        }

        using (var conn = new SqliteConnection(_spool))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS spool (id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS rejected (id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL, at TEXT NOT NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            var batch = Take(1000);
            await FlushSpoolAsync(stoppingToken);
            if (batch is not null && await SendAsync(batch, stoppingToken) == Delivery.Retry)
            {
                Spool(batch);
            }
        }
    }

    /// <summary>On shutdown, what's still in memory goes to the spool instead of being lost (review: Frank #7, Carl #2).</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        while (Enabled && Take(1000) is { } rest)
        {
            Spool(rest);
        }
    }

    private UseBatch? Take(int max)
    {
        var uses = new List<UseReport>();
        while (uses.Count < max && _queue.Reader.TryRead(out var use)) uses.Add(use);
        return uses.Count == 0 ? null : new UseBatch(GatewayId, Guid.NewGuid().ToString("N"), uses);
    }

    private string GatewayId => _options.Value.CredentialAuthority.GatewayId is { Length: > 0 } id ? id : Environment.MachineName;

    private enum Delivery { Delivered, Retry, Rejected }

    private async Task<Delivery> SendAsync(UseBatch batch, CancellationToken ct)
    {
        try
        {
            var response = await _http.CreateClient(CredentialSync.HttpClientName).PostAsJsonAsync("internal/audit", batch, CredentialSync.Json, ct);
            if (response.IsSuccessStatusCode) return Delivery.Delivered;
            if ((int)response.StatusCode is >= 400 and < 500 and not 408 and not 429)
            {
                // A batch the Authority will never take must not block every batch behind it (review: Frank #7).
                _logger.LogError("Audit batch {Batch} rejected by the Authority ({Status}); setting it aside.", batch.BatchId, (int)response.StatusCode);
                return Delivery.Rejected;
            }

            return Delivery.Retry;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Audit delivery failed ({Message}); spooling {Count} event(s).", ex.Message, batch.Uses.Count);
            return Delivery.Retry;
        }
    }

    private void Spool(UseBatch batch)
    {
        using var conn = new SqliteConnection(_spool);
        conn.Open();
        using var tx = conn.BeginTransaction();
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO spool (body) VALUES ($b)";
            insert.Parameters.AddWithValue("$b", JsonSerializer.Serialize(batch, CredentialSync.Json));
            insert.ExecuteNonQuery();
        }

        using (var trim = conn.CreateCommand())
        {
            // Bound by batches of up to 1000 events.
            trim.CommandText = "DELETE FROM spool WHERE id <= (SELECT MAX(id) FROM spool) - $keep";
            trim.Parameters.AddWithValue("$keep", Math.Max(1, _options.Value.CredentialAuthority.AuditSpoolLimit / 1000));
            if (trim.ExecuteNonQuery() > 0)
            {
                _logger.LogError("Audit spool full: dropped the oldest undelivered use events.");
            }
        }

        tx.Commit();
    }

    /// <summary>Keeps a batch the Authority refused, for an operator to inspect, out of the delivery path.</summary>
    private void SetAside(string body)
    {
        using var conn = new SqliteConnection(_spool);
        conn.Open();
        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO rejected (body, at) VALUES ($b, $t)";
        insert.Parameters.AddWithValue("$b", body);
        insert.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
    }

    private async Task FlushSpoolAsync(CancellationToken ct)
    {
        while (true)
        {
            long id;
            string body;
            using (var conn = new SqliteConnection(_spool))
            {
                conn.Open();
                using var read = conn.CreateCommand();
                read.CommandText = "SELECT id, body FROM spool ORDER BY id LIMIT 1";
                using var r = read.ExecuteReader();
                if (!r.Read()) return;
                (id, body) = (r.GetInt64(0), r.GetString(1));
            }

            var spooled = JsonSerializer.Deserialize<UseBatch>(body, CredentialSync.Json);
            switch (spooled is null ? Delivery.Rejected : await SendAsync(spooled, ct))
            {
                case Delivery.Retry:
                    return;
                case Delivery.Rejected:
                    SetAside(body);
                    break;
            }

            using var done = new SqliteConnection(_spool);
            done.Open();
            using var delete = done.CreateCommand();
            delete.CommandText = "DELETE FROM spool WHERE id = $id";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }
    }
}

/// <summary>
/// The HTTP handler for every call to the Authority (#245 FR6): presents this gateway's client certificate and
/// verifies the Authority by pinned fingerprint, or by a configured CA. A mismatch fails the call; there is no
/// fallback to cleartext or to no verification. Built per handler lifetime, so rotated certificate files are picked
/// up without a restart.
/// </summary>
public static class AuthorityTls
{
    public static HttpMessageHandler Handler(CredentialAuthorityOptions o)
    {
        var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true };
        if (o.ClientCertificatePath.Length > 0)
        {
            handler.SslOptions.ClientCertificates =
                [Domain.TlsPin.Load(o.ClientCertificatePath, o.ClientKeyPath, o.ClientCertificatePassword)];
        }

        if (o.ServerFingerprints.Length > 0)
        {
            var pins = o.ServerFingerprints.Select(Domain.TlsPin.Normalize).ToArray();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                Domain.TlsPin.Matches(certificate, pins, DateTimeOffset.UtcNow, Domain.TlsPin.ServerAuth);
        }
        else if (o.ServerCaPath.Length > 0)
        {
            // A CA file holds no private key: CreateFromPemFile would demand one and throw (review: Carl #1).
            var ca = Domain.TlsPin.LoadCertificate(o.ServerCaPath);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch)
                    || errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable))
                {
                    return false;
                }

                using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
                chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                return chain.Build(new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate));
            };
        }

        return handler;
    }
}
