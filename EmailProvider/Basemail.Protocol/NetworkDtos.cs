namespace Basemail.Protocol;

public sealed record BasemailNodeCapabilitiesDto(
    bool SmtpIngress,
    bool MailStorage,
    bool MailIndex,
    bool WebGateway,
    int AdvertisedStorageGb,
    int AdvertisedBandwidthGbPerDay);

public sealed record BasemailAppMemoryDto(
    long WorkingSetBytes,
    long PrivateMemoryBytes);

public sealed record BasemailAppCpuDto(
    long TotalProcessorTimeMs);

public sealed record BasemailNodeHealthDto(
    double UptimeScore,
    long StorageAvailableBytes,
    BasemailAppMemoryDto AppMemory,
    BasemailAppCpuDto AppCpu);

public sealed record BasemailNodeStatusResponse(
    string NodeId,
    string Operator,
    BasemailNodeCapabilitiesDto Capabilities,
    BasemailNodeHealthDto Health);

public sealed record BasemailParsedHeaderDto(
    string Name,
    string Value);

public sealed record BasemailCanonicalMessagePackage(
    string MailboxId,
    string MessageId,
    string ContentHash,
    string EnvelopeFrom,
    IReadOnlyList<string> EnvelopeRecipients,
    IReadOnlyList<BasemailParsedHeaderDto> Headers,
    string? PlainTextBody,
    string? HtmlBody,
    string? RawMessage,
    DateTimeOffset ReceivedAtUtc);

public sealed record BasemailIngressAcceptedResponse(
    bool Accepted,
    string MessageId,
    IReadOnlyList<string> SelectedReplicaNodes);

public sealed record BasemailReplicaMetadata(
    string EnvelopeFrom,
    IReadOnlyList<string> DeliveredAddresses,
    DateTimeOffset ReceivedAtUtc);

public sealed record BasemailReplicaWriteRequest(
    string MailboxId,
    string ContentHash,
    string RawMessage,
    BasemailReplicaMetadata Metadata);

public sealed record BasemailReplicaStoredResponse(
    bool Stored,
    string MessageId,
    string StorageProofStub);

public sealed record BasemailMailboxIndexEntry(
    string MessageId,
    string? ThreadId,
    DateTimeOffset ReceivedAtUtc,
    string? Subject,
    string Preview);

public sealed record BasemailMailboxIndexResponse(
    string MailboxId,
    int IndexVersion,
    IReadOnlyList<BasemailMailboxIndexEntry> Messages);
