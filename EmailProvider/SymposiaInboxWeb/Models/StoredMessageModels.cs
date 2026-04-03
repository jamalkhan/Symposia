namespace InboxWeb;

public sealed record ParsedEmailHeader(
    string Name,
    string Value);

public sealed record MailAuthenticationAwareness(
    string? SpfStatus,
    string? DkimStatus,
    string? DmarcStatus,
    bool HasDkimSignature,
    string? AuthenticationResultsHeader,
    string? ReceivedSpfHeader);

public sealed record StoredMailboxMessageMetadata(
    string MessageId,
    string MailboxId,
    string StorageProviderName,
    string EnvelopeFrom,
    IReadOnlyList<string> EnvelopeRecipients,
    IReadOnlyList<string> DeliveredAddresses,
    IReadOnlyList<string> DeliveredDomains,
    string? HeaderFrom,
    string? HeaderTo,
    string? Subject,
    IReadOnlyList<ParsedEmailHeader> Headers,
    string? PlainTextBody,
    string? HtmlBody,
    MailAuthenticationAwareness AuthenticationAwareness,
    DateTimeOffset ReceivedAtUtc);

public sealed record MailboxAddressPointer(
    string MailboxId,
    string StorageProviderName,
    string DomainName,
    string Address,
    DateTimeOffset UpdatedAtUtc);
