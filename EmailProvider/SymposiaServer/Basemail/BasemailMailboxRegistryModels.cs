namespace NativeSmtpReceiver;

public sealed class BasemailRoutingConfiguration
{
    public List<BasemailMailboxRouteConfiguration> Mailboxes { get; init; } = new();
}

public sealed class BasemailMailboxRouteConfiguration
{
    public string MailboxId { get; init; } = string.Empty;
    public List<string> Addresses { get; init; } = new();
    public List<string> ReplicaNodes { get; init; } = new();
    public string? StorageProviderName { get; init; }
    public long? Version { get; init; }

    public BasemailMailboxRouteDefinition Normalize()
    {
        return new BasemailMailboxRouteDefinition(
            MailboxId.Trim(),
            Addresses
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .Select(static address => address.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ReplicaNodes
                .Where(static nodeId => !string.IsNullOrWhiteSpace(nodeId))
                .Select(static nodeId => nodeId.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            string.IsNullOrWhiteSpace(StorageProviderName) ? null : StorageProviderName.Trim(),
            Version.GetValueOrDefault(1));
    }
}

public sealed record BasemailMailboxRouteDefinition(
    string MailboxId,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> ReplicaNodes,
    string? StorageProviderName,
    long Version = 1);

public sealed record BasemailMailboxRegistrySnapshot(
    string NodeId,
    long Version,
    bool IsDelta,
    long BaseVersion,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<BasemailMailboxRouteDefinition> Routes);

public sealed record BasemailMailboxRegistryVersion(
    string NodeId,
    long Version,
    DateTimeOffset GeneratedAtUtc);

public sealed record BasemailMailboxRegistryInvalidation(
    string NodeId,
    string OriginNodeId,
    long Version,
    int HopCount,
    int MaxHopCount,
    DateTimeOffset OccurredAtUtc);

public sealed record BasemailMailboxRegistryStats(
    long RegistryVersion,
    long DeltaSyncFetchCount,
    long NotificationsSent,
    long SuppressedNotifications,
    long DedupedInvalidations);
