using Microsoft.Extensions.Options;
using Symposia.Database.ComputeNode.Databases;

namespace Symposia.Database.ComputeNode.Supervision;

/// <summary>
/// Owns the lifecycle of the three co-located processes described in the spec: a single
/// multi-tenant pageserver, a single multi-tenant safekeeper, and one Postgres process per
/// hosted tenant database, started/stopped on demand via the local control API (POST/DELETE
/// /databases). This is the "supervisor" from the architectural plan — orchestration and
/// trust-boundary enforcement around the vendored Neon components, not a reimplementation of
/// page redirection or WAL quorum logic.
/// </summary>
public sealed class ComputeNodeSupervisor : IDisposable
{
    private readonly ComputeNodeOptions _options;
    private readonly IProcessLauncher _launcher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TenantDatabase> _databases = [];
    private readonly Dictionary<string, ManagedProcess> _postgresProcesses = [];

    private ManagedProcess? _pageserver;
    private ManagedProcess? _safekeeper;

    public ComputeNodeSupervisor(
        IOptions<ComputeNodeOptions> options,
        IProcessLauncher launcher,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _launcher = launcher;
        _loggerFactory = loggerFactory;
    }

    public void Start()
    {
        lock (_gate)
        {
            _pageserver = new ManagedProcess(
                "pageserver", _options.PageserverExecutablePath, arguments: "",
                _launcher, _options, _loggerFactory.CreateLogger("ComputeNode.Pageserver"));
            _pageserver.Start();

            _safekeeper = new ManagedProcess(
                "safekeeper", _options.SafekeeperExecutablePath, arguments: "",
                _launcher, _options, _loggerFactory.CreateLogger("ComputeNode.Safekeeper"));
            _safekeeper.Start();
        }
    }

    // ── Health / readiness (FR6) ──────────────────────────────────────────────

    public bool IsLive => _pageserver is not null && _safekeeper is not null;

    /// <summary>
    /// Healthy means both singleton processes (pageserver, safekeeper) are running or restarting,
    /// not permanently unhealthy. Individual crashed-and-recovering processes don't fail health;
    /// a crash-looped process that gave up does.
    /// </summary>
    public bool IsHealthy
    {
        get
        {
            lock (_gate)
            {
                return _pageserver?.State != ProcessState.Unhealthy
                    && _safekeeper?.State != ProcessState.Unhealthy
                    && _postgresProcesses.Values.All(p => p.State != ProcessState.Unhealthy);
            }
        }
    }

    /// <summary>Ready to accept new placements: healthy and under the declared capacity limit (#90).</summary>
    public bool CanAcceptNewPlacements
    {
        get
        {
            lock (_gate)
            {
                return IsHealthy && _databases.Count < _options.MaxHostedDatabases;
            }
        }
    }

    public IReadOnlyDictionary<string, ProcessState> ComponentStates
    {
        get
        {
            lock (_gate)
            {
                var states = new Dictionary<string, ProcessState>
                {
                    ["pageserver"] = _pageserver?.State ?? ProcessState.Stopped,
                    ["safekeeper"] = _safekeeper?.State ?? ProcessState.Stopped,
                };
                foreach (var (id, process) in _postgresProcesses)
                    states[$"postgres:{id}"] = process.State;
                return states;
            }
        }
    }

    // ── Local control API (POST/DELETE/GET /databases) ───────────────────────

    public IReadOnlyCollection<TenantDatabase> ListDatabases()
    {
        lock (_gate)
        {
            return _databases.Values.ToList();
        }
    }

    public TenantDatabase? GetDatabase(string tenantDatabaseId)
    {
        lock (_gate)
        {
            return _databases.GetValueOrDefault(tenantDatabaseId);
        }
    }

    public PlaceDatabaseResult PlaceDatabase(PlaceDatabaseRequest request)
    {
        lock (_gate)
        {
            if (_databases.ContainsKey(request.TenantDatabaseId))
                return PlaceDatabaseResult.Conflict();

            if (request.PgVersion != _options.SupportedPostgresMajorVersion)
                return PlaceDatabaseResult.UnsupportedVersion(_options.SupportedPostgresMajorVersion);

            if (!IsHealthy || _databases.Count >= _options.MaxHostedDatabases)
                return PlaceDatabaseResult.CapacityExceeded();

            var environment = new Dictionary<string, string>
            {
                ["SYMPOSIA_BLOB_BUCKET_CREDENTIAL"] = request.BlobBucketCredential,
                ["SYMPOSIA_TENANT_DATABASE_ID"] = request.TenantDatabaseId,
            };

            var process = new ManagedProcess(
                $"postgres:{request.TenantDatabaseId}",
                _options.PostgresExecutablePath,
                arguments: "",
                _launcher,
                _options,
                _loggerFactory.CreateLogger($"ComputeNode.Postgres.{request.TenantDatabaseId}"),
                environment);
            process.Start();

            _postgresProcesses[request.TenantDatabaseId] = process;
            var database = new TenantDatabase(
                request.TenantDatabaseId,
                request.PgVersion,
                request.Extensions,
                request.SafekeeperPeers,
                TenantDatabaseState.Running,
                request.BlobBucketUrl,
                request.BlobBucketCredential);
            _databases[request.TenantDatabaseId] = database;

            return PlaceDatabaseResult.Ok(database);
        }
    }

    public bool RemoveDatabase(string tenantDatabaseId)
    {
        lock (_gate)
        {
            if (!_databases.Remove(tenantDatabaseId))
                return false;

            if (_postgresProcesses.Remove(tenantDatabaseId, out var process))
                process.Stop();

            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pageserver?.Stop();
            _safekeeper?.Stop();
            foreach (var process in _postgresProcesses.Values)
                process.Stop();
        }
    }
}
