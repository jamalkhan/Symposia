using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>How the gateway may reach a storage node, or why it may not.</summary>
public sealed record AdmitResult(StorageNodeDescriptor? Node, string? RefusedBecause, IReadOnlyList<string> Warnings)
{
    public static AdmitResult Refuse(string reason, IReadOnlyList<string> warnings) => new(null, reason, warnings);
}

/// <summary>
/// The one place that decides which storage nodes this gateway may use (issue #145). The rules come
/// from the network's on-chain protocol activations, so every gateway applies the same policy:
/// <list type="bullet">
/// <item>a <c>storage.*</c> feature in effect is required: nodes that don't advertise it are refused;</item>
/// <item>a scheduled one that hasn't taken effect only produces a readiness warning;</item>
/// <item>likewise for the minimum protocol version;</item>
/// <item>the best transport both sides speak is chosen (pinned TLS over cleartext), and a TLS offer
/// whose proof doesn't verify refuses the node rather than falling back to cleartext.</item>
/// </list>
/// Per-gateway switches such as the removed <c>Gateway:RequireStorageTls</c> must not come back
/// (enforced by a test, #146).
/// </summary>
public interface INetworkPolicy
{
    ProtocolSchedule Current { get; }

    /// <summary>True once <paramref name="feature"/>'s activation is in effect.</summary>
    bool IsActive(string feature);

    AdmitResult AdmitStorageNode(string node, NodeServices services);
}

public sealed class NetworkPolicy : INetworkPolicy
{
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<NetworkPolicy> _logger;
    private readonly Metadata.GatewayMetadataStore? _store;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private ProtocolSchedule _schedule = ProtocolSchedule.None;

    // Fail closed (AC8): requirements seen in effect stay in effect, whatever later reads say, so an
    // unreachable or misbehaving Blockchain Gateway can't relax them. Persisted (when a store is given)
    // so a restart during a Blockchain Gateway outage doesn't relax them either (#145 review).
    private readonly HashSet<string> _seenActive = new(StringComparer.Ordinal);
    private int _seenMinVersion;

    public NetworkPolicy(IOptions<GatewayOptions> options, ILogger<NetworkPolicy> logger,
        Metadata.GatewayMetadataStore? store = null, TimeProvider? clock = null)
    {
        _options = options;
        _logger = logger;
        _store = store;
        _clock = clock ?? TimeProvider.System;
        if (store is not null)
        {
            var (features, minVersion) = store.LoadProtocolRequirements();
            _seenActive.UnionWith(features);
            _seenMinVersion = minVersion;
            if (features.Count > 0 || minVersion > 0)
            {
                logger.LogInformation("Protocol requirements remembered from earlier runs: {Features}; minimum version {Min}.",
                    string.Join(", ", features), minVersion);
            }
        }
    }

    public ProtocolSchedule Current
    {
        get { lock (_lock) return _schedule; }
    }

    /// <summary>Applies a freshly read schedule. Returns true if anything changed.</summary>
    internal bool Apply(ProtocolSchedule schedule)
    {
        schedule = schedule.Sanitized();
        List<string> newlyActive;
        int minVersion;
        bool changed;
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            newlyActive = schedule.Features.Where(f => f.InEffect(now) && _seenActive.Add(f.Name)).Select(f => f.Name).ToList();
            var previousMin = _seenMinVersion;
            _seenMinVersion = Math.Max(_seenMinVersion, schedule.MinProtocolVersion.At(now));
            minVersion = _seenMinVersion;
            if (minVersion != previousMin)
            {
                newlyActive.Add("");
            }

            // Records compare lists by reference, so compare the parts that matter ("now" always moves).
            changed = _schedule.Configured != schedule.Configured
                || _schedule.MinProtocolVersion != schedule.MinProtocolVersion
                || !_schedule.Features.SequenceEqual(schedule.Features);
            _schedule = schedule;
        }

        if (newlyActive.Count > 0)
        {
            _store?.SaveProtocolRequirements(newlyActive.Where(f => f.Length > 0), minVersion);
        }

        return changed;
    }

    public bool IsActive(string feature)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            return _seenActive.Contains(feature) || _schedule.Features.Any(f => f.Name == feature && f.InEffect(now));
        }
    }

    public AdmitResult AdmitStorageNode(string node, NodeServices services)
    {
        // One consistent snapshot for the whole decision.
        ProtocolSchedule schedule;
        string[] seen;
        int floor;
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            schedule = _schedule;
            seen = [.. _seenActive];
            floor = _seenMinVersion;
        }

        var storage = services.Storage;
        if (storage is null)
        {
            return AdmitResult.Refuse("it offers no storage service", []);
        }

        var verdict = ProtocolRequirements.EvaluateStorageNode(services, schedule, now, seen, floor);
        if (verdict.Refused)
        {
            return AdmitResult.Refuse(string.Join("; ", verdict.RefusedBecause), verdict.Upcoming);
        }

        if (storage.TlsUrl is { } tlsUrl)
        {
            // Pin only a fingerprint the node's registry key vouches for (#142). A bad proof is an attack
            // or a broken node; never a reason to fall back to cleartext (#145 QA TC-32).
            var o = _options.Value;
            var invalid = StorageTlsProof.Validate(o.ChainId, o.NodeRegistryAddress, node, storage.TlsFingerprint,
                storage.TlsNotAfter, storage.TlsProof, now);
            return invalid is null
                ? new AdmitResult(new StorageNodeDescriptor(node, tlsUrl, storage.TlsFingerprint!.ToLowerInvariant()), null, verdict.Upcoming)
                : AdmitResult.Refuse($"it offers TLS storage but {invalid}", verdict.Upcoming);
        }

        return storage.PlaintextUrl is { } plaintextUrl
            ? new AdmitResult(new StorageNodeDescriptor(node, plaintextUrl), null, verdict.Upcoming)
            : AdmitResult.Refuse("it advertises no storage gRPC address", verdict.Upcoming);
    }

    internal void LogUnconfiguredOnce() =>
        _logger.LogInformation("This network has no ProtocolActivations contract; no protocol features are required.");
}

/// <summary>
/// Keeps <see cref="NetworkPolicy"/> current from the Blockchain Gateway's <see cref="ProtocolSchedule.Path"/>,
/// on the registry refresh cadence. Registered before <see cref="NodeSourceSyncService"/> so the first node
/// sync already applies the policy.
/// </summary>
public sealed class ProtocolScheduleSyncService(
    NetworkPolicy policy,
    IHttpClientFactory httpFactory,
    IOptions<GatewayOptions> options,
    ILogger<ProtocolScheduleSyncService> logger) : BackgroundService
{
    public const string HttpClientName = "protocol-schedule";

    private bool _loggedUnconfigured;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        first.CancelAfter(TimeSpan.FromSeconds(5));
        await RefreshAsync(first.Token, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.RegistryRefreshSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken, stoppingToken);
        }
    }

    /// <param name="ct">Bounds this read.</param>
    /// <param name="stopping">The host shutting down; only then does cancellation escape.</param>
    private async Task RefreshAsync(CancellationToken ct, CancellationToken stopping)
    {
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            var url = options.Value.BlockchainGatewayUrl.TrimEnd('/') + ProtocolSchedule.Path;
            var schedule = await http.GetFromJsonAsync<ProtocolSchedule>(url, ct) ?? ProtocolSchedule.None;
            if (!schedule.Configured && !_loggedUnconfigured)
            {
                _loggedUnconfigured = true;
                policy.LogUnconfiguredOnce();
            }

            if (policy.Apply(schedule))
            {
                logger.LogInformation("Protocol schedule: minimum version {Min}; activations {Features}.",
                    schedule.MinProtocolVersion.Effective,
                    string.Join(", ", schedule.Features.Select(f => f.ActivatesAt == 0 ? $"{f.Name} (cancelled)"
                        : $"{f.Name} at {DateTimeOffset.FromUnixTimeSeconds(f.ActivatesAt):u}")));
            }
        }
        catch (Exception ex) when (!stopping.IsCancellationRequested)
        {
            // Anything else too (#145 review): a bad answer must never stop the gateway; requirements
            // already in effect stay enforced.
            logger.LogWarning(ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException ? null : ex,
                "Could not read the protocol schedule ({Error}); keeping the last known one.", ex.Message);
        }
    }
}
