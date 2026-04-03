using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class MailboxReadService
{
    private readonly HostingDirectory _hostingDirectory;
    private readonly IReadOnlyDictionary<string, IMailboxStorageProvider> _providers;
    private readonly ILogger<MailboxReadService> _logger;

    public MailboxReadService(
        HostingDirectory hostingDirectory,
        ILoggerFactory loggerFactory,
        ILogger<MailboxReadService> logger)
    {
        _hostingDirectory = hostingDirectory;
        _providers = MailboxStorageProviderFactory.CreateProviders(hostingDirectory, loggerFactory);
        _logger = logger;
    }

    public IReadOnlyList<MailboxBinding> GetMailboxBindings(string mailboxId)
    {
        return _hostingDirectory.GetMailboxBindings(mailboxId);
    }

    public async Task<IReadOnlyList<MailboxMessageSummary>> ListMessagesAsync(string mailboxId, CancellationToken cancellationToken = default)
    {
        var bindings = _hostingDirectory.GetMailboxBindings(mailboxId);
        if (bindings.Count == 0)
        {
            _logger.LogInformation("Mailbox {MailboxId} has no configured bindings", mailboxId);
            return Array.Empty<MailboxMessageSummary>();
        }

        var summaries = new List<MailboxMessageSummary>();
        foreach (var providerName in bindings.Select(static binding => binding.StorageProviderName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_providers.TryGetValue(providerName, out var provider))
            {
                throw new InvalidOperationException($"Storage provider '{providerName}' was not resolved for mailbox '{mailboxId}'.");
            }

            _logger.LogInformation("Listing messages for mailbox {MailboxId} via provider {ProviderName}", mailboxId, providerName);
            var providerSummaries = await provider.ListMessagesAsync(mailboxId, cancellationToken);
            summaries.AddRange(providerSummaries);
        }

        return summaries
            .OrderByDescending(static summary => summary.ReceivedAtUtc)
            .ThenByDescending(static summary => summary.MessageId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<StoredMailboxMessage?> GetMessageAsync(string mailboxId, string messageId, CancellationToken cancellationToken = default)
    {
        var bindings = _hostingDirectory.GetMailboxBindings(mailboxId);
        if (bindings.Count == 0)
        {
            _logger.LogInformation("Mailbox {MailboxId} has no configured bindings", mailboxId);
            return null;
        }

        foreach (var providerName in bindings.Select(static binding => binding.StorageProviderName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_providers.TryGetValue(providerName, out var provider))
            {
                throw new InvalidOperationException($"Storage provider '{providerName}' was not resolved for mailbox '{mailboxId}'.");
            }

            var message = await provider.GetMessageAsync(mailboxId, messageId, cancellationToken);
            if (message is not null)
            {
                _logger.LogInformation(
                    "Loaded message {MessageId} for mailbox {MailboxId} via provider {ProviderName}",
                    messageId,
                    mailboxId,
                    providerName);
                return message;
            }
        }

        _logger.LogInformation("Message {MessageId} was not found for mailbox {MailboxId}", messageId, mailboxId);
        return null;
    }
}
