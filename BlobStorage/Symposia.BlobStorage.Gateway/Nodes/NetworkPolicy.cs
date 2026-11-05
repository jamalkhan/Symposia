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

public sealed class NetworkPolicy(IOptions<GatewayOptions> options, ILogger<NetworkPolicy> logger, TimeProvider? clock = null) : INetworkPolicy
{
    private const string StorageScope = "storage.";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private ProtocolSchedule _schedule = ProtocolSchedule.None;

    // Fail closed (AC8): requirements seen in effect stay in effect for this process, whatever
    // later reads say, so an unreachable or misbehaving Blockchain Gateway can't relax them.
    private readonly HashSet<string> _seenActive = new(StringComparer.Ordinal);
    private int _seenMinVersion;

    public ProtocolSchedule Current
    {
        get { lock (_lock) return _schedule; }
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>Applies a freshly read schedule. Returns true if anything changed.</summary>
    public bool Apply(ProtocolSchedule schedule)
    {
        lock (_lock)
        {
            var now = Now;
            foreach (var f in schedule.Features.Where(f => f.InEffect(now)))
            {
                _seenActive.Add(f.Name);
            }

            _seenMinVersion = Math.Max(_seenMinVersion, schedule.MinProtocolVersion.At(now));
            // Records compare lists by reference, so compare the parts that matter ("now" always moves).
            var changed = _schedule.Configured != schedule.Configured
                || _schedule.MinProtocolVersion != schedule.MinProtocolVersion
                || !_schedule.Features.SequenceEqual(schedule.Features);
            _schedule = schedule;
            return changed;
        }
    }

    public bool IsActive(string feature)
    {
        lock (_lock)
        {
            return _seenActive.Contains(feature) || _schedule.Features.Any(f => f.Name == feature && f.InEffect(Now));
        }
    }

    private int MinVersionNow()
    {
        lock (_lock)
        {
            return Math.Max(_seenMinVersion, _schedule.MinProtocolVersion.At(Now));
        }
    }

    public AdmitResult AdmitStorageNode(string node, NodeServices services)
    {
        var warnings = new List<string>();
        var storage = services.Storage;
        if (storage is null)
        {
            return AdmitResult.Refuse("it offers no storage service", warnings);
        }

        var tlsUrl = storage.TlsUrl;
        var plaintextUrl = storage.PlaintextUrl;
        var features = Advertised(services, tlsUrl, plaintextUrl);
        var now = Now;
        ProtocolSchedule schedule;
        lock (_lock)
        {
            schedule = _schedule;
        }

        var required = schedule.Features.Where(f => f.Name.StartsWith(StorageScope, StringComparison.Ordinal))
            .Select(f => f.Name).Where(IsActive).Union(SeenActiveStorageFeatures()).ToList();
        var missing = required.Where(f => !features.Contains(f)).ToList();
        if (missing.Count > 0)
        {
            return AdmitResult.Refuse($"it lacks required protocol feature(s) {string.Join(", ", missing)}", warnings);
        }

        var minVersion = MinVersionNow();
        if (services.ProtocolVersion < minVersion)
        {
            return AdmitResult.Refuse($"it speaks protocol version {services.ProtocolVersion}, below the network minimum {minVersion}", warnings);
        }

        foreach (var upcoming in schedule.Features.Where(f => f.Name.StartsWith(StorageScope, StringComparison.Ordinal)
                     && f.ActivatesAt > 0 && !f.InEffect(now) && !features.Contains(f.Name)))
        {
            warnings.Add($"it lacks {upcoming.Name}, which becomes required at {DateTimeOffset.FromUnixTimeSeconds(upcoming.ActivatesAt):u}");
        }

        var pending = schedule.MinProtocolVersion;
        if (pending.Pending > services.ProtocolVersion && pending.PendingAt > now.ToUnixTimeSeconds())
        {
            warnings.Add($"it speaks protocol version {services.ProtocolVersion}; version {pending.Pending} becomes the minimum at {DateTimeOffset.FromUnixTimeSeconds(pending.PendingAt):u}");
        }

        if (tlsUrl is not null)
        {
            // Pin only a fingerprint the node's registry key vouches for (#142). A bad proof is an attack
            // or a broken node; never a reason to fall back to cleartext (#145 QA TC-32).
            var o = options.Value;
            var invalid = StorageTlsProof.Validate(o.ChainId, o.NodeRegistryAddress, node, storage.TlsFingerprint,
                storage.TlsNotAfter, storage.TlsProof, now);
            return invalid is null
                ? new AdmitResult(new StorageNodeDescriptor(node, tlsUrl, storage.TlsFingerprint!.ToLowerInvariant()), null, warnings)
                : AdmitResult.Refuse($"it offers TLS storage but {invalid}", warnings);
        }

        return plaintextUrl is not null
            ? new AdmitResult(new StorageNodeDescriptor(node, plaintextUrl), null, warnings)
            : AdmitResult.Refuse("it advertises no storage gRPC address", warnings);
    }

    private IEnumerable<string> SeenActiveStorageFeatures()
    {
        lock (_lock)
        {
            return _seenActive.Where(f => f.StartsWith(StorageScope, StringComparison.Ordinal)).ToList();
        }
    }

    /// <summary>
    /// The features a node offers. Documents from before #145 carry no list; infer the transports from
    /// the URLs they publish so those nodes are judged on what they actually serve.
    /// </summary>
    private static HashSet<string> Advertised(NodeServices services, string? tlsUrl, string? plaintextUrl)
    {
        var features = new HashSet<string>(services.Features ?? [], StringComparer.Ordinal);
        if (services.Features is null)
        {
            if (tlsUrl is not null)
            {
                features.Add(ProtocolFeatures.StorageGrpcTls);
            }

            if (plaintextUrl is not null)
            {
                features.Add(ProtocolFeatures.StorageGrpcPlaintext);
            }
        }

        return features;
    }

    internal void LogUnconfiguredOnce()
    {
        logger.LogInformation("This network has no ProtocolActivations contract; no protocol features are required.");
    }
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
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException
                                   && !stopping.IsCancellationRequested)
        {
            logger.LogWarning("Could not read the protocol schedule ({Error}); keeping the last known one.", ex.Message);
        }
    }
}
