using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class MailboxDeliveryService
{
    private readonly IReadOnlyDictionary<string, IMailboxStorageProvider> _providers;
    private readonly ILogger<MailboxDeliveryService> _logger;

    public MailboxDeliveryService(
        MailboxStorageProviderCatalog providerCatalog,
        ILogger<MailboxDeliveryService> logger)
    {
        _providers = providerCatalog.Providers;
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

        await StorePreparedDeliveriesAsync(deliveries, cancellationToken);
    }

    public async Task StorePreparedDeliveriesAsync(IReadOnlyList<MailboxStorageDelivery> deliveries, CancellationToken cancellationToken = default)
    {
        foreach (var delivery in deliveries)
        {
            try
            {
                await StoreSingleDeliveryAsync(delivery, cancellationToken);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                throw new TransientMailboxDeliveryException(
                    $"Transient failure while delivering mailbox '{delivery.MailboxId}'.",
                    deliveries,
                    ex);
            }
            catch (NotSupportedException ex)
            {
                throw new PermanentMailboxDeliveryException(
                    $"Permanent failure while delivering mailbox '{delivery.MailboxId}'.",
                    ex);
            }
        }
    }

    private async Task StoreSingleDeliveryAsync(MailboxStorageDelivery delivery, CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(delivery.StorageProviderName, out var provider))
        {
            throw new PermanentMailboxDeliveryException(
                $"Storage provider '{delivery.StorageProviderName}' was not resolved for mailbox '{delivery.MailboxId}'.",
                new InvalidOperationException($"Storage provider '{delivery.StorageProviderName}' was not resolved for mailbox '{delivery.MailboxId}'."));
        }

        _logger.LogInformation(
            "Delivering message {MessageId} for mailbox {MailboxId} to {AddressCount} routed addresses via provider {ProviderName} ({ProviderType})",
            delivery.Message.MessageId,
            delivery.MailboxId,
            delivery.Routes.Count,
            provider.Name,
            provider.Type);
        await provider.StoreAsync(delivery, cancellationToken);
    }

    private static bool IsTransient(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or TimeoutException;
    }
}
