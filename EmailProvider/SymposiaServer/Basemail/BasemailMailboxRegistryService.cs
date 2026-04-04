using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Basemail.Protocol;

namespace NativeSmtpReceiver;

public sealed class BasemailMailboxRegistryService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly BasemailNodeOptions _nodeOptions;
    private readonly HostingDirectory _hostingDirectory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BasemailMailboxRegistryService> _logger;
    private readonly Dictionary<string, BasemailMailboxRouteDefinition> _routesByMailboxId;
    private readonly Dictionary<string, BasemailInvalidationTracker> _receivedInvalidationsByOrigin = new(StringComparer.Ordinal);
    private readonly string _defaultStorageProviderName;
    private readonly Lock _syncLock = new();
    private long _registryVersion;
    private long _deltaSyncFetchCount;
    private long _notificationsSent;
    private long _suppressedNotifications;
    private long _dedupedInvalidations;
    private long _lastBroadcastVersion;
    private DateTimeOffset _lastBroadcastAtUtc;

    public BasemailMailboxRegistryService(
        BasemailNodeOptions nodeOptions,
        HostingDirectory hostingDirectory,
        IHttpClientFactory httpClientFactory,
        ILogger<BasemailMailboxRegistryService> logger)
    {
        _nodeOptions = nodeOptions;
        _hostingDirectory = hostingDirectory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _routesByMailboxId = LoadSeedRoutes();
        _defaultStorageProviderName = ResolveDefaultStorageProviderName(hostingDirectory);
        _registryVersion = _routesByMailboxId.Count == 0 ? 0 : _routesByMailboxId.Values.Max(static route => route.Version);
    }

    public int RouteCount
    {
        get
        {
            lock (_syncLock)
            {
                return _routesByMailboxId.Count;
            }
        }
    }

    public long RegistryVersion
    {
        get
        {
            lock (_syncLock)
            {
                return _registryVersion;
            }
        }
    }

    public IReadOnlyList<BasemailMailboxRouteDefinition> GetKnownRoutes()
    {
        lock (_syncLock)
        {
            return _routesByMailboxId.Values
                .OrderBy(static route => route.MailboxId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public BasemailMailboxRegistryStats GetStats()
    {
        lock (_syncLock)
        {
            return new BasemailMailboxRegistryStats(
                _registryVersion,
                _deltaSyncFetchCount,
                _notificationsSent,
                _suppressedNotifications,
                _dedupedInvalidations);
        }
    }

    public BasemailMailboxRegistryVersion GetVersion()
    {
        lock (_syncLock)
        {
            return new BasemailMailboxRegistryVersion(
                _nodeOptions.NodeId,
                _registryVersion,
                DateTimeOffset.UtcNow);
        }
    }

    public BasemailMailboxRegistrySnapshot GetSnapshot(long? sinceVersion = null)
    {
        lock (_syncLock)
        {
            var effectiveSinceVersion = sinceVersion.GetValueOrDefault(0);
            var isDelta = sinceVersion.HasValue;
            var routes = _routesByMailboxId.Values
                .Where(route => !sinceVersion.HasValue || route.Version > effectiveSinceVersion)
                .OrderBy(static route => route.MailboxId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new BasemailMailboxRegistrySnapshot(
                _nodeOptions.NodeId,
                _registryVersion,
                isDelta,
                effectiveSinceVersion,
                DateTimeOffset.UtcNow,
                routes);
        }
    }

    public IReadOnlyList<MailboxBinding> GetLocalBindings(string mailboxId)
    {
        var localBindings = _hostingDirectory.GetMailboxBindings(mailboxId);
        if (localBindings.Count > 0)
        {
            return localBindings;
        }

        var route = GetKnownRoute(mailboxId);
        if (route is null || !route.ReplicaNodes.Contains(_nodeOptions.NodeId, StringComparer.Ordinal))
        {
            return Array.Empty<MailboxBinding>();
        }

        var storageProviderName = string.IsNullOrWhiteSpace(route.StorageProviderName)
            ? _defaultStorageProviderName
            : route.StorageProviderName;

        return route.Addresses
            .Select(address => new MailboxBinding(
                route.MailboxId,
                address,
                GetDomain(address),
                storageProviderName))
            .ToArray();
    }

    public async Task<BasemailMailboxRouteDefinition?> GetRouteAsync(string mailboxId, CancellationToken cancellationToken)
    {
        var route = GetKnownRoute(mailboxId);
        if (route is not null)
        {
            return route;
        }

        foreach (var peer in _nodeOptions.Peers)
        {
            if (string.IsNullOrWhiteSpace(peer.BaseUrl))
            {
                continue;
            }

            var replicatedRoute = await TryFetchRouteFromPeerAsync(peer, mailboxId, cancellationToken);
            if (replicatedRoute is null)
            {
                continue;
            }

            lock (_syncLock)
            {
                MergeRouteUnsafe(replicatedRoute);
            }

            _logger.LogInformation(
                "Fetched Basemail mailbox route {MailboxId} from peer {PeerNodeId}",
                replicatedRoute.MailboxId,
                peer.NodeId);

            return replicatedRoute;
        }

        return null;
    }

    public IReadOnlyList<MailboxRoute> ResolveLocalRoutes(string mailboxId, IReadOnlyList<string> deliveredAddresses)
    {
        var bindings = GetLocalBindings(mailboxId);
        if (bindings.Count == 0)
        {
            return Array.Empty<MailboxRoute>();
        }

        var effectiveBindings = deliveredAddresses.Count == 0
            ? bindings
            : bindings
                .Where(binding => deliveredAddresses.Contains(binding.Address, StringComparer.OrdinalIgnoreCase))
                .ToArray();

        if (effectiveBindings.Count == 0)
        {
            effectiveBindings = bindings;
        }

        return effectiveBindings
            .Select(binding => new MailboxRoute(binding.Address, binding.MailboxId, binding.DomainName, binding.StorageProviderName))
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> SelectReplicaNodeIdsAsync(string mailboxId, CancellationToken cancellationToken)
    {
        var route = await GetRouteAsync(mailboxId, cancellationToken);
        if (route is not null && route.ReplicaNodes.Count > 0)
        {
            return route.ReplicaNodes;
        }

        return _nodeOptions.Peers
            .Select(static peer => peer.NodeId)
            .Prepend(_nodeOptions.NodeId)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
    }

    public async Task SyncFromPeersAsync(CancellationToken cancellationToken)
    {
        foreach (var peer in _nodeOptions.Peers)
        {
            if (string.IsNullOrWhiteSpace(peer.BaseUrl))
            {
                continue;
            }

            var localVersion = RegistryVersion;
            var peerVersion = await TryFetchVersionFromPeerAsync(peer, cancellationToken);
            if (peerVersion is null || peerVersion.Version <= localVersion)
            {
                continue;
            }

            var snapshot = await TryFetchSnapshotFromPeerAsync(peer, localVersion, cancellationToken);
            if (snapshot is null)
            {
                continue;
            }

            var mergedCount = 0;
            lock (_syncLock)
            {
                foreach (var route in snapshot.Routes)
                {
                    if (MergeRouteUnsafe(route))
                    {
                        mergedCount++;
                    }
                }
            }

            if (mergedCount > 0)
            {
                _logger.LogInformation(
                    "Merged {MergedCount} Basemail mailbox routes from peer {PeerNodeId} at registry version {RegistryVersion}",
                    mergedCount,
                    peer.NodeId,
                    snapshot.Version);

                await NotifyPeersOfCurrentVersionAsync(cancellationToken, exceptNodeId: peer.NodeId);
            }
        }
    }

    public async Task NotifyPeersOfCurrentVersionAsync(CancellationToken cancellationToken, string? exceptNodeId = null)
    {
        if (!_nodeOptions.NetworkEnabled || _nodeOptions.Peers.Count == 0)
        {
            return;
        }

        var currentVersion = RegistryVersion;
        if (currentVersion <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromSeconds(Math.Max(1, _nodeOptions.RegistryInvalidationCooldownSeconds));
        lock (_syncLock)
        {
            if (_lastBroadcastVersion >= currentVersion && now - _lastBroadcastAtUtc < cooldown)
            {
                _suppressedNotifications++;
                return;
            }

            _lastBroadcastVersion = currentVersion;
            _lastBroadcastAtUtc = now;
        }

        var invalidation = new BasemailMailboxRegistryInvalidation(
            _nodeOptions.NodeId,
            _nodeOptions.NodeId,
            currentVersion,
            0,
            Math.Max(1, _nodeOptions.RegistryInvalidationMaxHops),
            now);

        await BroadcastInvalidationAsync(invalidation, cancellationToken, exceptNodeId);
    }

    public async Task HandleInvalidationAsync(BasemailMailboxRegistryInvalidation invalidation, CancellationToken cancellationToken)
    {
        if (invalidation.Version <= RegistryVersion)
        {
            lock (_syncLock)
            {
                _dedupedInvalidations++;
            }
            return;
        }

        var peer = _nodeOptions.Peers.FirstOrDefault(candidate =>
            string.Equals(candidate.NodeId, invalidation.NodeId, StringComparison.Ordinal));
        if (peer is null || string.IsNullOrWhiteSpace(peer.BaseUrl))
        {
            _logger.LogWarning(
                "Received Basemail mailbox registry invalidation for unknown peer {PeerNodeId}",
                invalidation.NodeId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var dedupWindow = TimeSpan.FromSeconds(Math.Max(1, _nodeOptions.RegistryInvalidationDedupSeconds));
        var invalidationKey = string.IsNullOrWhiteSpace(invalidation.OriginNodeId)
            ? invalidation.NodeId
            : invalidation.OriginNodeId;
        lock (_syncLock)
        {
            if (_receivedInvalidationsByOrigin.TryGetValue(invalidationKey, out var tracker) &&
                tracker.Version >= invalidation.Version &&
                now - tracker.ObservedAtUtc < dedupWindow)
            {
                _dedupedInvalidations++;
                return;
            }

            _receivedInvalidationsByOrigin[invalidationKey] = new BasemailInvalidationTracker(invalidation.Version, now);
        }

        var snapshot = await TryFetchSnapshotFromPeerAsync(peer, RegistryVersion, cancellationToken);
        if (snapshot is null)
        {
            return;
        }

        var mergedCount = 0;
        lock (_syncLock)
        {
            foreach (var route in snapshot.Routes)
            {
                if (MergeRouteUnsafe(route))
                {
                    mergedCount++;
                }
            }
        }

        if (mergedCount > 0)
        {
            _logger.LogInformation(
                "Applied {MergedCount} Basemail mailbox routes after invalidation from peer {PeerNodeId} at registry version {RegistryVersion}",
                mergedCount,
                peer.NodeId,
                snapshot.Version);

            if (invalidation.HopCount + 1 < invalidation.MaxHopCount)
            {
                var forwardedInvalidation = invalidation with
                {
                    NodeId = _nodeOptions.NodeId,
                    OriginNodeId = string.IsNullOrWhiteSpace(invalidation.OriginNodeId) ? invalidation.NodeId : invalidation.OriginNodeId,
                    HopCount = invalidation.HopCount + 1,
                    OccurredAtUtc = DateTimeOffset.UtcNow
                };
                await BroadcastInvalidationAsync(forwardedInvalidation, cancellationToken, exceptNodeId: peer.NodeId);
            }
        }
    }

    private async Task BroadcastInvalidationAsync(
        BasemailMailboxRegistryInvalidation invalidation,
        CancellationToken cancellationToken,
        string? exceptNodeId = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(invalidation, SerializerOptions);
        var candidatePeers = SelectInvalidationPeers(invalidation, exceptNodeId);

        foreach (var peer in candidatePeers)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(nameof(BasemailMailboxRegistryService));
                var relativePath = "/network/registry/invalidate";
                using var request = CreateSignedRequest(HttpMethod.Post, relativePath, body);
                request.RequestUri = new Uri(new Uri(peer.BaseUrl!, UriKind.Absolute), relativePath);
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using var response = await client.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                lock (_syncLock)
                {
                    _notificationsSent++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to notify peer {PeerNodeId} of Basemail mailbox registry invalidation at version {Version}",
                    peer.NodeId,
                    invalidation.Version);
            }
        }
    }

    private IReadOnlyList<BasemailPeerRecord> SelectInvalidationPeers(
        BasemailMailboxRegistryInvalidation invalidation,
        string? exceptNodeId)
    {
        var maxFanout = Math.Max(1, _nodeOptions.RegistryInvalidationFanout);
        var originNodeId = string.IsNullOrWhiteSpace(invalidation.OriginNodeId)
            ? invalidation.NodeId
            : invalidation.OriginNodeId;

        return _nodeOptions.Peers
            .Where(peer =>
                !string.IsNullOrWhiteSpace(peer.BaseUrl) &&
                !string.Equals(peer.NodeId, exceptNodeId, StringComparison.Ordinal) &&
                !string.Equals(peer.NodeId, invalidation.NodeId, StringComparison.Ordinal) &&
                !string.Equals(peer.NodeId, originNodeId, StringComparison.Ordinal))
            .OrderBy(peer => ComputeFanoutScore(peer.NodeId, originNodeId, invalidation.Version))
            .ThenBy(peer => peer.NodeId, StringComparer.Ordinal)
            .Take(maxFanout)
            .ToArray();
    }

    private static ulong ComputeFanoutScore(string peerNodeId, string originNodeId, long version)
    {
        return (ulong)HashCode.Combine(peerNodeId, originNodeId, version);
    }

    private BasemailMailboxRouteDefinition? GetKnownRoute(string mailboxId)
    {
        lock (_syncLock)
        {
            return _routesByMailboxId.TryGetValue(mailboxId, out var route)
                ? route
                : null;
        }
    }

    private bool MergeRouteUnsafe(BasemailMailboxRouteDefinition route)
    {
        if (_routesByMailboxId.TryGetValue(route.MailboxId, out var existing) &&
            existing.Version >= route.Version)
        {
            return false;
        }

        _routesByMailboxId[route.MailboxId] = route;
        _registryVersion = Math.Max(_registryVersion, route.Version);
        return true;
    }

    private async Task<BasemailMailboxRouteDefinition?> TryFetchRouteFromPeerAsync(
        BasemailPeerRecord peer,
        string mailboxId,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(BasemailMailboxRegistryService));
            var relativePath = $"/network/registry/mailboxes/{Uri.EscapeDataString(mailboxId)}";
            using var request = CreateSignedRequest(HttpMethod.Get, relativePath);
            request.RequestUri = new Uri(new Uri(peer.BaseUrl!, UriKind.Absolute), relativePath);

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<BasemailMailboxRouteDefinition>(stream, SerializerOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to fetch Basemail mailbox route {MailboxId} from peer {PeerNodeId}",
                mailboxId,
                peer.NodeId);
            return null;
        }
    }

    private async Task<BasemailMailboxRegistryVersion?> TryFetchVersionFromPeerAsync(
        BasemailPeerRecord peer,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(BasemailMailboxRegistryService));
            var relativePath = "/network/registry/version";
            using var request = CreateSignedRequest(HttpMethod.Get, relativePath);
            request.RequestUri = new Uri(new Uri(peer.BaseUrl!, UriKind.Absolute), relativePath);

            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<BasemailMailboxRegistryVersion>(stream, SerializerOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to fetch Basemail mailbox registry version from peer {PeerNodeId}",
                peer.NodeId);
            return null;
        }
    }

    private async Task<BasemailMailboxRegistrySnapshot?> TryFetchSnapshotFromPeerAsync(
        BasemailPeerRecord peer,
        long sinceVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            lock (_syncLock)
            {
                _deltaSyncFetchCount++;
            }

            var client = _httpClientFactory.CreateClient(nameof(BasemailMailboxRegistryService));
            var relativePath = $"/network/registry/snapshot?sinceVersion={sinceVersion}";
            using var request = CreateSignedRequest(HttpMethod.Get, relativePath);
            request.RequestUri = new Uri(new Uri(peer.BaseUrl!, UriKind.Absolute), relativePath);

            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<BasemailMailboxRegistrySnapshot>(stream, SerializerOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to fetch Basemail mailbox registry snapshot from peer {PeerNodeId}",
                peer.NodeId);
            return null;
        }
    }

    private HttpRequestMessage CreateSignedRequest(HttpMethod method, string relativePath, byte[]? body = null)
    {
        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderProtocolVersion, BasemailProtocolConstants.ProtocolVersion);

        if (!_nodeOptions.HasSigningKeyPair)
        {
            return request;
        }

        body ??= Array.Empty<byte>();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var canonicalBytes = BasemailCanonicalRequest.GetCanonicalBytes(
            method.Method,
            relativePath,
            timestamp,
            nonce,
            body);
        var signature = BasemailSignature.Sign(canonicalBytes, _nodeOptions.PrivateKeyPem!);

        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderNode, _nodeOptions.NodeId);
        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderTimestamp, timestamp);
        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderNonce, nonce);
        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderSignature, signature);
        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderKeyId, _nodeOptions.NodeId);
        return request;
    }

    private Dictionary<string, BasemailMailboxRouteDefinition> LoadSeedRoutes()
    {
        var configuredPath = Environment.GetEnvironmentVariable("BASEMAIL_NETWORK_ROUTING_CONFIG");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return new Dictionary<string, BasemailMailboxRouteDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        var resolvedPath = PathResolution.ResolvePath(configuredPath);
        if (!File.Exists(resolvedPath))
        {
            return new Dictionary<string, BasemailMailboxRouteDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        using var stream = File.OpenRead(resolvedPath);
        var config = JsonSerializer.Deserialize<BasemailRoutingConfiguration>(stream, SerializerOptions);

        return config?.Mailboxes
            .Where(static route => !string.IsNullOrWhiteSpace(route.MailboxId))
            .Select(static route => route.Normalize())
            .ToDictionary(static route => route.MailboxId, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, BasemailMailboxRouteDefinition>(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveDefaultStorageProviderName(HostingDirectory hostingDirectory)
    {
        var preferredProvider = hostingDirectory.StorageProviders
            .Values
            .FirstOrDefault(static provider => string.Equals(provider.Type, MailStorageProviderTypes.FileSystem, StringComparison.OrdinalIgnoreCase))
            ?.Name;

        return preferredProvider
               ?? hostingDirectory.StorageProviders.Keys.FirstOrDefault()
               ?? "local-default";
    }

    private static string GetDomain(string address)
    {
        var atIndex = address.LastIndexOf('@');
        return atIndex >= 0 && atIndex < address.Length - 1
            ? address[(atIndex + 1)..]
            : "unknown.local";
    }
}

internal sealed record BasemailInvalidationTracker(
    long Version,
    DateTimeOffset ObservedAtUtc);
