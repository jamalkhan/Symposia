namespace NativeSmtpReceiver;

public sealed record StoredEmailMessage(
    string MessageId,
    string EnvelopeFrom,
    IReadOnlyList<string> EnvelopeRecipients,
    IReadOnlyList<string> DataLines,
    string RawMessage,
    DateTimeOffset ReceivedAtUtc);

public sealed record ParsedEmailHeader(
    string Name,
    string Value);

public sealed record ParsedMailboxMessage(
    string? HeaderFrom,
    string? HeaderTo,
    string? Subject,
    IReadOnlyList<ParsedEmailHeader> Headers,
    string? PlainTextBody,
    string? HtmlBody);

public sealed record MailboxStorageDelivery(
    string MailboxId,
    string StorageProviderName,
    IReadOnlyList<MailboxRoute> Routes,
    StoredEmailMessage Message);

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
    DateTimeOffset ReceivedAtUtc);

public sealed record MailboxAddressPointer(
    string MailboxId,
    string StorageProviderName,
    string DomainName,
    string Address,
    DateTimeOffset UpdatedAtUtc);

public sealed record MailboxMessageSummary(
    string MessageId,
    string MailboxId,
    string StorageProviderName,
    string EnvelopeFrom,
    IReadOnlyList<string> DeliveredAddresses,
    string? Subject,
    DateTimeOffset ReceivedAtUtc);

public sealed record StoredMailboxMessage(
    StoredMailboxMessageMetadata Metadata,
    string RawMessage);
