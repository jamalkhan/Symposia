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
/// Keeps <see cref="CredentialStore"/> current with the Credential Authority (#3 FR5.18), two ways at once:
/// a Watch stream pushes each new state version (sub-second), and a full pull every
/// <see cref="CredentialAuthorityOptions.PollSeconds"/> heals anything the stream missed (a reconnect, a
/// restart, a partition). Revocation therefore reaches this gateway within one poll interval at worst,
/// well inside the 60 s SLA. With no Authority configured, only configured credentials exist.
/// </summary>
public sealed class CredentialSync(CredentialStore cache, IOptions<GatewayOptions> options, IHttpClientFactory http, ILogger<CredentialSync> logger)
    : BackgroundService
{
    public const string HttpClientName = "credential-authority";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.CredentialAuthority;
        if (string.IsNullOrWhiteSpace(o.Url))
        {
            return;
        }

        await PullAsync(stoppingToken);
        await Task.WhenAll(WatchLoopAsync(stoppingToken), PollLoopAsync(stoppingToken));
    }

    /// <summary>Pulls and applies the full snapshot; true when it was newer.</summary>
    public async Task<bool> PullAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await http.CreateClient(HttpClientName).GetFromJsonAsync<AuthoritySnapshot>("internal/snapshot", Json, ct);
            if (snapshot is not null && cache.Apply(snapshot))
            {
                logger.LogDebug("Credential state now at version {Version}.", snapshot.Version);
                return true;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Credential Authority snapshot pull failed: {Message}. Keeping version {Version}.", ex.Message, cache.Version);
        }

        return false;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.CredentialAuthority.PollSeconds)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            await PullAsync(ct);
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
                    if (call.ResponseStream.Current.Version > cache.Version)
                    {
                        await PullAsync(ct);
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

/// <summary>One data-plane use, as reported to the Authority's audit log (#3 FR8.28).</summary>
public sealed record UseReport(string CredentialId, string Action, string? Resource, string? SourceIp, string? Region, string GatewayId,
    string Outcome, string? Reason, DateTimeOffset At);

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
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS spool (id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            var batch = new List<UseReport>();
            while (batch.Count < 1000 && _queue.Reader.TryRead(out var use)) batch.Add(use);
            await FlushSpoolAsync(stoppingToken);
            if (batch.Count > 0 && !await SendAsync(batch, stoppingToken))
            {
                Spool(batch);
            }
        }
    }

    private async Task<bool> SendAsync(IReadOnlyList<UseReport> batch, CancellationToken ct)
    {
        try
        {
            var response = await _http.CreateClient(CredentialSync.HttpClientName).PostAsJsonAsync("internal/audit", batch, CredentialSync.Json, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Audit delivery failed ({Message}); spooling {Count} event(s).", ex.Message, batch.Count);
            return false;
        }
    }

    private void Spool(IReadOnlyList<UseReport> batch)
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

            if (!await SendAsync(JsonSerializer.Deserialize<List<UseReport>>(body, CredentialSync.Json) ?? [], ct))
            {
                return;
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
