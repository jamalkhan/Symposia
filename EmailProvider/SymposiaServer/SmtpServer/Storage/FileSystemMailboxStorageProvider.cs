using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class FileSystemMailboxStorageProvider : IMailboxStorageProvider
{
    private readonly FileSystemStorageOptions _options;
    private readonly ILogger<FileSystemMailboxStorageProvider> _logger;

    public FileSystemMailboxStorageProvider(
        string name,
        FileSystemStorageOptions options,
        ILogger<FileSystemMailboxStorageProvider> logger)
    {
        Name = name;
        _options = options;
        _logger = logger;
    }

    public string Name { get; }
    public string Type => MailStorageProviderTypes.FileSystem;

    public async Task StoreAsync(MailboxStorageDelivery delivery, CancellationToken cancellationToken = default)
    {
        var parsedMessage = EmailMessageParser.Parse(delivery.Message.DataLines);
        var mailboxIdSegment = SanitizePathSegment(delivery.MailboxId);
        var mailboxMessagesPath = Path.Combine(_options.RootPath, "mailboxes", mailboxIdSegment, "messages");
        Directory.CreateDirectory(mailboxMessagesPath);

        var messageBasePath = Path.Combine(mailboxMessagesPath, delivery.Message.MessageId);
        var messagePath = $"{messageBasePath}.eml";
        var metadataPath = $"{messageBasePath}.json";

        _logger.LogInformation(
            "Persisting message {MessageId} for mailbox {MailboxId} to filesystem path {FilePath}",
            delivery.Message.MessageId,
            delivery.MailboxId,
            messagePath);
        await using (var writer = new StreamWriter(messagePath, false, Encoding.UTF8))
        {
            foreach (var line in delivery.Message.DataLines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(line);
            }
        }

        var metadata = new StoredMailboxMessageMetadata(
            delivery.Message.MessageId,
            delivery.MailboxId,
            delivery.StorageProviderName,
            delivery.Message.EnvelopeFrom,
            delivery.Message.EnvelopeRecipients,
            delivery.Routes.Select(static route => route.Address).ToArray(),
            delivery.Routes.Select(static route => route.DomainName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            parsedMessage.HeaderFrom,
            parsedMessage.HeaderTo,
            parsedMessage.Subject,
            parsedMessage.Headers,
            parsedMessage.PlainTextBody,
            parsedMessage.HtmlBody,
            parsedMessage.AuthenticationAwareness,
            delivery.Message.ReceivedAtUtc);
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        foreach (var route in delivery.Routes)
        {
            var pointerPath = Path.Combine(
                _options.RootPath,
                "addresses",
                SanitizePathSegment(route.DomainName),
                SanitizePathSegment(route.Address),
                "pointer.json");
            Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);

            var pointer = new MailboxAddressPointer(
                delivery.MailboxId,
                delivery.StorageProviderName,
                route.DomainName,
                route.Address,
                delivery.Message.ReceivedAtUtc);
            await File.WriteAllTextAsync(
                pointerPath,
                JsonSerializer.Serialize(pointer, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<MailboxMessageSummary>> ListMessagesAsync(string mailboxId, CancellationToken cancellationToken = default)
    {
        var mailboxMessagesPath = GetMailboxMessagesPath(mailboxId);
        if (!Directory.Exists(mailboxMessagesPath))
        {
            return Array.Empty<MailboxMessageSummary>();
        }

        var summaries = new List<MailboxMessageSummary>();
        foreach (var metadataPath in Directory.GetFiles(mailboxMessagesPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = await ReadMetadataAsync(metadataPath, cancellationToken);
            if (metadata is null)
            {
                continue;
            }

            summaries.Add(new MailboxMessageSummary(
                metadata.MessageId,
                metadata.MailboxId,
                metadata.StorageProviderName,
                metadata.EnvelopeFrom,
                metadata.DeliveredAddresses,
                metadata.Subject,
                metadata.ReceivedAtUtc));
        }

        return summaries
            .OrderByDescending(static summary => summary.ReceivedAtUtc)
            .ThenByDescending(static summary => summary.MessageId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<StoredMailboxMessage?> GetMessageAsync(string mailboxId, string messageId, CancellationToken cancellationToken = default)
    {
        var mailboxMessagesPath = GetMailboxMessagesPath(mailboxId);
        var messageBasePath = Path.Combine(mailboxMessagesPath, SanitizePathSegment(messageId));
        var metadataPath = $"{messageBasePath}.json";
        var messagePath = $"{messageBasePath}.eml";

        if (!File.Exists(metadataPath) || !File.Exists(messagePath))
        {
            return null;
        }

        var metadata = await ReadMetadataAsync(metadataPath, cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        var rawMessage = await File.ReadAllTextAsync(messagePath, cancellationToken);
        return new StoredMailboxMessage(metadata, rawMessage);
    }

    private async Task<StoredMailboxMessageMetadata?> ReadMetadataAsync(string metadataPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(metadataPath);
            return await JsonSerializer.DeserializeAsync<StoredMailboxMessageMetadata>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Skipping unreadable mailbox metadata at {MetadataPath}", metadataPath);
            return null;
        }
    }

    private string GetMailboxMessagesPath(string mailboxId)
    {
        return Path.Combine(_options.RootPath, "mailboxes", SanitizePathSegment(mailboxId), "messages");
    }

    private static string SanitizePathSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            builder.Append(invalidChars.Contains(c) ? '_' : c);
        }

        return builder.ToString();
    }
}
