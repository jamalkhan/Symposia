namespace InboxWeb;

public sealed record HostedMailboxDescriptor(
    string MailboxId,
    string Address,
    string DomainName,
    string StorageProviderName);

public sealed record InboxAccountRecord(
    string AccountId,
    string Address,
    string MailboxId,
    string DisplayName,
    string PasswordHash,
    string PasswordSalt,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<AddressBookContactRecord> Contacts);

public sealed class InboxAccountStoreDocument
{
    public List<InboxAccountRecord> Accounts { get; init; } = new();
}

public sealed record AddressBookContactRecord(
    string ContactId,
    string DisplayName,
    string EmailAddress,
    DateTimeOffset UpdatedAtUtc);

public sealed record InboxAccountSession(
    string AccountId,
    string Address,
    string MailboxId,
    string DisplayName);

public sealed record RegisterAccountRequest(
    string Username,
    string Domain,
    string Password,
    string? DisplayName);

public sealed record LoginRequest(
    string EmailAddress,
    string Password);

public sealed record ContactUpsertRequest(
    string? ContactId,
    string DisplayName,
    string EmailAddress);

public sealed record ComposeMessageRequest(
    string Subject,
    string To,
    string? Cc,
    string? Bcc,
    string? PlainTextBody,
    string? HtmlBody,
    string? ReplyToMessageId);

public sealed record MailboxMessageState(
    string Folder,
    bool IsRead,
    string Direction,
    string DeliveryStatus,
    DateTimeOffset UpdatedAtUtc);

public sealed record MailboxMessageListItem(
    string MessageId,
    string Folder,
    bool IsRead,
    string Direction,
    string DeliveryStatus,
    string DisplayFrom,
    IReadOnlyList<string> DisplayTo,
    string? Subject,
    string Preview,
    DateTimeOffset ReceivedAtUtc);

public sealed record MailboxMessageDetail(
    string MessageId,
    string Folder,
    bool IsRead,
    string Direction,
    string DeliveryStatus,
    string EnvelopeFrom,
    IReadOnlyList<string> EnvelopeRecipients,
    IReadOnlyList<string> DeliveredAddresses,
    string? HeaderFrom,
    string? HeaderTo,
    string? Subject,
    IReadOnlyList<ParsedEmailHeader> Headers,
    string? PlainTextBody,
    string? HtmlBody,
    string RawMessage,
    DateTimeOffset ReceivedAtUtc);

public sealed record MailboxFolderCounts(
    int Inbox,
    int Sent,
    int Trash);

public sealed record MailboxBootstrapResponse(
    InboxAccountSession Account,
    IReadOnlyList<string> HostedDomains,
    IReadOnlyList<AddressBookContactRecord> Contacts,
    MailboxFolderCounts Counts,
    IReadOnlyList<MailboxMessageListItem> RecentMessages);

public sealed record ComposeMessageResult(
    string SentMessageId,
    int DeliveredLocalCount,
    int QueuedExternalCount);
