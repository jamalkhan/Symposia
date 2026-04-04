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
    IReadOnlyList<AddressBookContactRecord> Contacts,
    InboxAccountSecurityState SecurityState);

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
    string DisplayName,
    string CsrfToken);

public sealed record InboxAccountSecurityState(
    int FailedLoginCount,
    DateTimeOffset? LockoutUntilUtc,
    DateTimeOffset? LastLoginAtUtc,
    DateTimeOffset? LastFailedLoginAtUtc,
    string? PasswordResetTokenHash,
    DateTimeOffset? PasswordResetRequestedAtUtc,
    DateTimeOffset? LastPasswordChangedAtUtc);

public sealed record RegisterAccountRequest(
    string Username,
    string Domain,
    string Password,
    string? DisplayName);

public sealed record LoginRequest(
    string EmailAddress,
    string Password);

public sealed record PasswordResetRequest(
    string EmailAddress);

public sealed record PasswordResetConfirmationRequest(
    string Token,
    string NewPassword);

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
    string ThreadId,
    bool IsStarred,
    IReadOnlyList<string> Labels,
    DateTimeOffset UpdatedAtUtc);

public sealed record MailboxMessageListItem(
    string MessageId,
    string Folder,
    bool IsRead,
    string Direction,
    string DeliveryStatus,
    string ThreadId,
    bool IsStarred,
    IReadOnlyList<string> Labels,
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
    string ThreadId,
    bool IsStarred,
    IReadOnlyList<string> Labels,
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

public sealed record MailboxMessageQuery(
    string Folder,
    string? Query,
    string? Label,
    int Page,
    int PageSize);

public sealed record MailboxMessagePage(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<MailboxMessageListItem> Items);

public sealed record MailboxThreadSummary(
    string ThreadId,
    string? Subject,
    IReadOnlyList<string> Participants,
    string Preview,
    int MessageCount,
    int UnreadCount,
    bool HasStarredMessage,
    DateTimeOffset LatestReceivedAtUtc,
    string LatestMessageId);

public sealed record MailboxThreadPage(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<MailboxThreadSummary> Items);

public sealed record MailboxThreadDetail(
    string ThreadId,
    string? Subject,
    IReadOnlyList<MailboxMessageDetail> Messages);

public sealed record MailboxBootstrapResponse(
    InboxAccountSession Account,
    IReadOnlyList<string> HostedDomains,
    IReadOnlyList<AddressBookContactRecord> Contacts,
    MailboxFolderCounts Counts,
    MailboxMessagePage RecentMessages);

public sealed record ComposeMessageResult(
    string SentMessageId,
    int DeliveredLocalCount,
    int QueuedExternalCount);

public sealed record LabelUpdateRequest(
    IReadOnlyList<string> Labels);

public sealed record StarUpdateRequest(
    bool IsStarred);

public sealed record PasswordResetResponse(
    bool Accepted,
    string? ResetToken);

public sealed record InboxAuthenticationResult(
    bool Succeeded,
    bool IsLockedOut,
    string? ErrorMessage,
    InboxAccountSession? Session);
