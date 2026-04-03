using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class MailboxDeliveryService
{
    private readonly IReadOnlyDictionary<string, IMailboxStorageProvider> _providers;
    private readonly ILogger<MailboxDeliveryService> _logger;

    public MailboxDeliveryService(
        HostingDirectory hostingDirectory,
        ILoggerFactory loggerFactory,
        ILogger<MailboxDeliveryService> logger)
    {
        _providers = MailboxStorageProviderFactory.CreateProviders(hostingDirectory, loggerFactory);
        _logger = logger;
    }

    public async Task StoreAsync(string envelopeFrom, IReadOnlyList<MailboxRoute> recipients, IReadOnlyList<string> dataLines, CancellationToken cancellationToken = default)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var message = new StoredEmailMessage(
            Guid.NewGuid().ToString("N"),
            envelopeFrom,
            recipients.Select(static recipient => recipient.Address).ToArray(),
            dataLines.ToArray(),
            string.Join("\r\n", dataLines),
            receivedAt);

        var deliveries = recipients
            .GroupBy(static recipient => $"{recipient.StorageProviderName}\u001F{recipient.MailboxId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new MailboxStorageDelivery(
                group.First().MailboxId,
                group.First().StorageProviderName,
                group.ToArray(),
                message))
            .ToArray();

        foreach (var delivery in deliveries)
        {
            if (!_providers.TryGetValue(delivery.StorageProviderName, out var provider))
            {
                throw new InvalidOperationException($"Storage provider '{delivery.StorageProviderName}' was not resolved for mailbox '{delivery.MailboxId}'.");
            }

            _logger.LogInformation(
                "Delivering message {MessageId} for mailbox {MailboxId} to {AddressCount} routed addresses via provider {ProviderName} ({ProviderType})",
                message.MessageId,
                delivery.MailboxId,
                delivery.Routes.Count,
                provider.Name,
                provider.Type);
            await provider.StoreAsync(delivery, cancellationToken);
        }
    }
}
