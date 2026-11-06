namespace Symposia.Network.Node;

/// <summary>What the network's activations say about one node: refused (and why), and upcoming gaps.</summary>
public sealed record RequirementsVerdict(IReadOnlyList<string> RefusedBecause, IReadOnlyList<string> Upcoming)
{
    public bool Refused => RefusedBecause.Count > 0;
}

/// <summary>
/// The one implementation of "does this node meet the network's protocol requirements" (issue #145),
/// shared by gateways (which enforce it) and the Node Host's Network tab (which reports it), so the two
/// can't disagree. Transport choice and TLS proof checks stay with the gateway.
/// </summary>
public static class ProtocolRequirements
{
    /// <summary>Feature names are namespaced by role; a storage node is judged only on <c>storage.*</c> activations.</summary>
    public const string StorageScope = "storage.";

    /// <param name="alreadyActive">Requirements the caller has seen in effect before (fail closed), even if <paramref name="schedule"/> no longer lists them.</param>
    /// <param name="minVersionFloor">Highest minimum version the caller has seen in effect.</param>
    public static RequirementsVerdict EvaluateStorageNode(
        NodeServices services,
        ProtocolSchedule schedule,
        DateTimeOffset now,
        IReadOnlyCollection<string>? alreadyActive = null,
        int minVersionFloor = 0)
    {
        var advertised = Advertised(services);
        var refused = new List<string>();
        var upcoming = new List<string>();

        var active = schedule.Features.Where(f => f.InEffect(now)).Select(f => f.Name)
            .Concat(alreadyActive ?? [])
            .Where(InScope)
            .Distinct(StringComparer.Ordinal);
        var missing = active.Where(f => !advertised.Contains(f)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            refused.Add($"it lacks required protocol feature(s) {string.Join(", ", missing)}");
        }

        var minVersion = Math.Max(minVersionFloor, schedule.MinProtocolVersion.At(now));
        if (services.ProtocolVersion < minVersion)
        {
            refused.Add($"it speaks protocol version {services.ProtocolVersion}, below the network minimum {minVersion}");
        }

        foreach (var f in schedule.Features.Where(f => InScope(f.Name) && f.ActivatesAt > 0 && !f.InEffect(now) && !advertised.Contains(f.Name))
                     .OrderBy(f => f.ActivatesAt))
        {
            upcoming.Add($"it lacks {f.Name}, which becomes required at {DateTimeOffset.FromUnixTimeSeconds(f.ActivatesAt):u}");
        }

        var pending = schedule.MinProtocolVersion;
        if (pending.Pending > services.ProtocolVersion && pending.PendingAt > now.ToUnixTimeSeconds())
        {
            upcoming.Add($"it speaks protocol version {services.ProtocolVersion}; version {pending.Pending} becomes the minimum at {DateTimeOffset.FromUnixTimeSeconds(pending.PendingAt):u}");
        }

        return new RequirementsVerdict(refused, upcoming);
    }

    private static bool InScope(string feature) => feature.StartsWith(StorageScope, StringComparison.Ordinal);

    /// <summary>
    /// The features a node offers. Documents from before #145 carry no list; infer the transports from
    /// the URLs they publish so those nodes are judged on what they actually serve. Remove the inference
    /// when <see cref="ProtocolFeatures.StorageGrpcPlaintext"/> is retired.
    /// </summary>
    public static HashSet<string> Advertised(NodeServices services)
    {
        var features = new HashSet<string>(services.Features ?? [], StringComparer.Ordinal);
        if (services.Features is null && services.Storage is { } storage)
        {
            if (storage.TlsUrl is not null)
            {
                features.Add(ProtocolFeatures.StorageGrpcTls);
            }

            if (storage.PlaintextUrl is not null)
            {
                features.Add(ProtocolFeatures.StorageGrpcPlaintext);
            }
        }

        return features;
    }
}
