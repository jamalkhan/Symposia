namespace NativeSmtpReceiver;

public interface IMailboxStorageProvider
{
    string Name { get; }
    string Type { get; }

    Task StoreAsync(MailboxStorageDelivery delivery, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailboxMessageSummary>> ListMessagesAsync(string mailboxId, CancellationToken cancellationToken = default);
    Task<StoredMailboxMessage?> GetMessageAsync(string mailboxId, string messageId, CancellationToken cancellationToken = default);
}
