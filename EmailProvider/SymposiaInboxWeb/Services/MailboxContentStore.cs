using System.Text;
using System.Text.Json;

namespace InboxWeb;

public sealed class MailboxContentStore
{
    private static readonly MailAuthenticationAwareness EmptyAuthentication = new(
        null,
        null,
        null,
        false,
        null,
        null);

    private readonly HostedMailboxRepository _mailboxRepository;
    private readonly ILogger<MailboxContentStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MailboxContentStore(
        HostedMailboxRepository mailboxRepository,
        ILogger<MailboxContentStore> logger)
    {
        _mailboxRepository = mailboxRepository;
        _logger = logger;
    }

    public async Task<MailboxFolderCounts> GetFolderCountsAsync(string mailboxId, CancellationToken cancellationToken = default)
    {
        var messages = await LoadMessagesAsync(mailboxId, null, cancellationToken);
        return new MailboxFolderCounts(
            messages.Count(static message => string.Equals(message.State.Folder, "inbox", StringComparison.OrdinalIgnoreCase)),
            messages.Count(static message => string.Equals(message.State.Folder, "sent", StringComparison.OrdinalIgnoreCase)),
            messages.Count(static message => string.Equals(message.State.Folder, "trash", StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<IReadOnlyList<MailboxMessageListItem>> ListMessagesAsync(
        string mailboxId,
        string folder,
        string? query,
        CancellationToken cancellationToken = default)
    {
        var normalizedFolder = string.IsNullOrWhiteSpace(folder) ? "inbox" : folder.Trim().ToLowerInvariant();
        var messages = await LoadMessagesAsync(mailboxId, normalizedFolder, cancellationToken);
        if (!string.IsNullOrWhiteSpace(query))
        {
            messages = messages
                .Where(message => BuildSearchText(message.Metadata).Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return messages
            .OrderByDescending(static message => message.Metadata.ReceivedAtUtc)
            .ThenByDescending(static message => message.Metadata.MessageId, StringComparer.Ordinal)
            .Select(ToListItem)
            .ToArray();
    }

    public async Task<MailboxMessageDetail?> GetMessageAsync(
        string mailboxId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var pathInfo = await ResolveMailboxPathsAsync(mailboxId, cancellationToken);
        var basePath = Path.Combine(pathInfo.MessagesPath, SanitizePathSegment(messageId));
        var metadata = await ReadMetadataAsync($"{basePath}.json", cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        var state = await ReadStateAsync($"{basePath}.state.json", metadata, cancellationToken);
        var rawMessage = File.Exists($"{basePath}.eml")
            ? await File.ReadAllTextAsync($"{basePath}.eml", cancellationToken)
            : string.Empty;

        return new MailboxMessageDetail(
            metadata.MessageId,
            state.Folder,
            state.IsRead,
            state.Direction,
            state.DeliveryStatus,
            metadata.EnvelopeFrom,
            metadata.EnvelopeRecipients,
            metadata.DeliveredAddresses,
            metadata.HeaderFrom,
            metadata.HeaderTo,
            metadata.Subject,
            metadata.Headers,
            metadata.PlainTextBody,
            metadata.HtmlBody,
            rawMessage,
            metadata.ReceivedAtUtc);
    }

    public Task<bool> MarkReadAsync(string mailboxId, string messageId, bool isRead, CancellationToken cancellationToken = default)
    {
        return UpdateStateAsync(mailboxId, messageId, state => state with
        {
            IsRead = isRead,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public Task<bool> MoveToFolderAsync(string mailboxId, string messageId, string folder, CancellationToken cancellationToken = default)
    {
        var normalizedFolder = folder.Trim().ToLowerInvariant();
        return UpdateStateAsync(mailboxId, messageId, state => state with
        {
            Folder = normalizedFolder,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task<ComposeMessageResult> ComposeAsync(
        InboxAccountSession account,
        ComposeMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        var recipients = ParseRecipients(request.To, request.Cc, request.Bcc);
        if (recipients.Count == 0)
        {
            throw new InvalidOperationException("At least one recipient is required.");
        }

        var senderPaths = await ResolveMailboxPathsAsync(account.MailboxId, cancellationToken);
        var messageId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var subject = string.IsNullOrWhiteSpace(request.Subject) ? "(no subject)" : request.Subject.Trim();
        var plainTextBody = request.PlainTextBody ?? string.Empty;
        var htmlBody = string.IsNullOrWhiteSpace(request.HtmlBody) ? null : request.HtmlBody;
        var rfcMessageId = $"<{messageId}@symposia.local>";
        var headers = BuildHeaders(account.Address, request, subject, rfcMessageId, now);
        var rawMessage = BuildRawMessage(account.Address, request, subject, plainTextBody, htmlBody, rfcMessageId, now);

        var sentMetadata = new StoredMailboxMessageMetadata(
            messageId,
            account.MailboxId,
            senderPaths.ProviderName,
            account.Address,
            recipients,
            recipients,
            recipients.Select(GetDomainName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            account.Address,
            request.To,
            subject,
            headers,
            plainTextBody,
            htmlBody,
            EmptyAuthentication,
            now);

        await WriteMailboxMessageAsync(
            senderPaths.MessagesPath,
            sentMetadata,
            rawMessage,
            new MailboxMessageState("sent", true, "outbound", "sent", now),
            cancellationToken);

        var directory = await _mailboxRepository.LoadDirectoryAsync(cancellationToken);
        var localRoutes = new List<(MailboxRoute Route, string StorageRoot)>();
        var externalRecipients = new List<string>();

        foreach (var recipient in recipients.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (directory.TryResolveRecipient(recipient, out var route, out _))
            {
                localRoutes.Add((route, ResolveFileSystemRoot(directory, route.StorageProviderName, route.MailboxId)));
            }
            else
            {
                externalRecipients.Add(recipient);
            }
        }

        foreach (var group in localRoutes.GroupBy(static item => $"{item.Route.StorageProviderName}\u001f{item.Route.MailboxId}", StringComparer.OrdinalIgnoreCase))
        {
            var routeGroup = group.ToArray();
            var sample = routeGroup[0];
            var metadata = new StoredMailboxMessageMetadata(
                messageId,
                sample.Route.MailboxId,
                sample.Route.StorageProviderName,
                account.Address,
                recipients,
                routeGroup.Select(static item => item.Route.Address).ToArray(),
                routeGroup.Select(static item => item.Route.DomainName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                account.Address,
                request.To,
                subject,
                headers,
                plainTextBody,
                htmlBody,
                EmptyAuthentication,
                now);
            var recipientMessagesPath = Path.Combine(sample.StorageRoot, "mailboxes", SanitizePathSegment(sample.Route.MailboxId), "messages");
            await WriteMailboxMessageAsync(
                recipientMessagesPath,
                metadata,
                rawMessage,
                new MailboxMessageState("inbox", false, "inbound", "delivered", now),
                cancellationToken);

            foreach (var item in routeGroup)
            {
                await WritePointerAsync(sample.StorageRoot, item.Route, now, cancellationToken);
            }
        }

        if (externalRecipients.Count > 0)
        {
            var outboundPath = Path.Combine(senderPaths.StorageRoot, "outbound", "pending");
            Directory.CreateDirectory(outboundPath);
            await File.WriteAllTextAsync(
                Path.Combine(outboundPath, $"{messageId}.json"),
                JsonSerializer.Serialize(new
                {
                    messageId,
                    from = account.Address,
                    recipients = externalRecipients,
                    request.Cc,
                    request.Bcc,
                    subject,
                    plainTextBody,
                    htmlBody,
                    queuedAtUtc = now
                }, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            _logger.LogInformation("Queued {RecipientCount} external recipients for message {MessageId}", externalRecipients.Count, messageId);
        }

        return new ComposeMessageResult(messageId, localRoutes.Count, externalRecipients.Count);
    }

    private async Task<List<LoadedMailboxMessage>> LoadMessagesAsync(
        string mailboxId,
        string? folderFilter,
        CancellationToken cancellationToken)
    {
        var pathInfo = await ResolveMailboxPathsAsync(mailboxId, cancellationToken);
        if (!Directory.Exists(pathInfo.MessagesPath))
        {
            return new List<LoadedMailboxMessage>();
        }

        var messages = new List<LoadedMailboxMessage>();
        foreach (var metadataPath in Directory.GetFiles(pathInfo.MessagesPath, "*.json", SearchOption.TopDirectoryOnly)
                     .Where(static path => !path.EndsWith(".state.json", StringComparison.OrdinalIgnoreCase)))
        {
            var metadata = await ReadMetadataAsync(metadataPath, cancellationToken);
            if (metadata is null)
            {
                continue;
            }

            var state = await ReadStateAsync(Path.ChangeExtension(metadataPath, null) + ".state.json", metadata, cancellationToken);
            if (folderFilter is not null &&
                !string.Equals(folderFilter, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state.Folder, folderFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            messages.Add(new LoadedMailboxMessage(metadata, state));
        }

        return messages;
    }

    private async Task<bool> UpdateStateAsync(
        string mailboxId,
        string messageId,
        Func<MailboxMessageState, MailboxMessageState> updater,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pathInfo = await ResolveMailboxPathsAsync(mailboxId, cancellationToken);
            var basePath = Path.Combine(pathInfo.MessagesPath, SanitizePathSegment(messageId));
            var metadata = await ReadMetadataAsync($"{basePath}.json", cancellationToken);
            if (metadata is null)
            {
                return false;
            }

            var statePath = $"{basePath}.state.json";
            var currentState = await ReadStateAsync(statePath, metadata, cancellationToken);
            await WriteStateAsync(statePath, updater(currentState), cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MailboxPathInfo> ResolveMailboxPathsAsync(string mailboxId, CancellationToken cancellationToken)
    {
        var directory = await _mailboxRepository.LoadDirectoryAsync(cancellationToken);
        var bindings = directory.GetMailboxBindings(mailboxId);
        if (bindings.Count == 0)
        {
            throw new InvalidOperationException($"Mailbox '{mailboxId}' is not configured.");
        }

        var providerName = bindings[0].StorageProviderName;
        var storageRoot = ResolveFileSystemRoot(directory, providerName, mailboxId);
        return new MailboxPathInfo(
            storageRoot,
            Path.Combine(storageRoot, "mailboxes", SanitizePathSegment(mailboxId), "messages"),
            providerName);
    }

    private static string ResolveFileSystemRoot(HostingDirectory directory, string providerName, string mailboxId)
    {
        if (!directory.StorageProviders.TryGetValue(providerName, out var configuration))
        {
            throw new InvalidOperationException($"Storage provider '{providerName}' was not resolved for mailbox '{mailboxId}'.");
        }

        if (!string.Equals(configuration.Type, MailStorageProviderTypes.FileSystem, StringComparison.OrdinalIgnoreCase) ||
            configuration.FileSystem is null)
        {
            throw new NotSupportedException($"The inbox web app currently supports filesystem-backed mailbox browsing only. Mailbox '{mailboxId}' uses provider '{providerName}'.");
        }

        return configuration.FileSystem.RootPath;
    }

    private static async Task<StoredMailboxMessageMetadata?> ReadMetadataAsync(string metadataPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(metadataPath);
        return await JsonSerializer.DeserializeAsync<StoredMailboxMessageMetadata>(stream, cancellationToken: cancellationToken);
    }

    private static async Task<MailboxMessageState> ReadStateAsync(
        string statePath,
        StoredMailboxMessageMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
        {
            return new MailboxMessageState("inbox", false, "inbound", "delivered", metadata.ReceivedAtUtc);
        }

        await using var stream = File.OpenRead(statePath);
        var state = await JsonSerializer.DeserializeAsync<MailboxMessageState>(stream, cancellationToken: cancellationToken);
        return state ?? new MailboxMessageState("inbox", false, "inbound", "delivered", metadata.ReceivedAtUtc);
    }

    private static Task WriteStateAsync(string statePath, MailboxMessageState state, CancellationToken cancellationToken)
    {
        return File.WriteAllTextAsync(
            statePath,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private static async Task WriteMailboxMessageAsync(
        string messagesPath,
        StoredMailboxMessageMetadata metadata,
        string rawMessage,
        MailboxMessageState state,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(messagesPath);
        var basePath = Path.Combine(messagesPath, SanitizePathSegment(metadata.MessageId));
        await File.WriteAllTextAsync($"{basePath}.eml", rawMessage, cancellationToken);
        await File.WriteAllTextAsync(
            $"{basePath}.json",
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        await WriteStateAsync($"{basePath}.state.json", state, cancellationToken);
    }

    private static Task WritePointerAsync(string storageRoot, MailboxRoute route, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pointerPath = Path.Combine(
            storageRoot,
            "addresses",
            SanitizePathSegment(route.DomainName),
            SanitizePathSegment(route.Address),
            "pointer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
        var pointer = new MailboxAddressPointer(route.MailboxId, route.StorageProviderName, route.DomainName, route.Address, now);
        return File.WriteAllTextAsync(
            pointerPath,
            JsonSerializer.Serialize(pointer, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private static string BuildRawMessage(
        string from,
        ComposeMessageRequest request,
        string subject,
        string plainTextBody,
        string? htmlBody,
        string rfcMessageId,
        DateTimeOffset now)
    {
        var builder = new StringBuilder();
        builder.Append("From: ").Append(from).Append("\r\n");
        builder.Append("To: ").Append(request.To).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(request.Cc))
        {
            builder.Append("Cc: ").Append(request.Cc).Append("\r\n");
        }

        builder.Append("Subject: ").Append(subject).Append("\r\n");
        builder.Append("Date: ").Append(now.ToString("R")).Append("\r\n");
        builder.Append("Message-ID: ").Append(rfcMessageId).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(request.ReplyToMessageId))
        {
            builder.Append("In-Reply-To: <").Append(request.ReplyToMessageId.Trim()).Append("@symposia.local>\r\n");
        }

        if (string.IsNullOrWhiteSpace(htmlBody))
        {
            builder.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
            builder.Append(plainTextBody);
        }
        else
        {
            var boundary = "symposia-" + Guid.NewGuid().ToString("N");
            builder.Append("MIME-Version: 1.0\r\n");
            builder.Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
            builder.Append("--").Append(boundary).Append("\r\n");
            builder.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
            builder.Append(plainTextBody).Append("\r\n");
            builder.Append("--").Append(boundary).Append("\r\n");
            builder.Append("Content-Type: text/html; charset=utf-8\r\n\r\n");
            builder.Append(htmlBody).Append("\r\n");
            builder.Append("--").Append(boundary).Append("--");
        }

        return builder.ToString();
    }

    private static IReadOnlyList<ParsedEmailHeader> BuildHeaders(
        string from,
        ComposeMessageRequest request,
        string subject,
        string messageId,
        DateTimeOffset now)
    {
        var headers = new List<ParsedEmailHeader>
        {
            new("From", from),
            new("To", request.To),
            new("Subject", subject),
            new("Date", now.ToString("R")),
            new("Message-ID", messageId)
        };

        if (!string.IsNullOrWhiteSpace(request.Cc))
        {
            headers.Add(new ParsedEmailHeader("Cc", request.Cc));
        }

        if (!string.IsNullOrWhiteSpace(request.ReplyToMessageId))
        {
            headers.Add(new ParsedEmailHeader("In-Reply-To", request.ReplyToMessageId));
        }

        return headers;
    }

    private static IReadOnlyList<string> ParseRecipients(string to, string? cc, string? bcc)
    {
        return new[] { to, cc, bcc }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(static value => value.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildSearchText(StoredMailboxMessageMetadata metadata)
    {
        return string.Join(
            "\n",
            new[]
            {
                metadata.Subject,
                metadata.EnvelopeFrom,
                metadata.HeaderFrom,
                metadata.HeaderTo,
                string.Join(" ", metadata.DeliveredAddresses),
                metadata.PlainTextBody,
                metadata.HtmlBody
            }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static MailboxMessageListItem ToListItem(LoadedMailboxMessage message)
    {
        return new MailboxMessageListItem(
            message.Metadata.MessageId,
            message.State.Folder,
            message.State.IsRead,
            message.State.Direction,
            message.State.DeliveryStatus,
            message.Metadata.HeaderFrom ?? message.Metadata.EnvelopeFrom,
            message.Metadata.EnvelopeRecipients,
            message.Metadata.Subject,
            BuildPreview(message.Metadata),
            message.Metadata.ReceivedAtUtc);
    }

    private static string BuildPreview(StoredMailboxMessageMetadata metadata)
    {
        var candidate = metadata.PlainTextBody;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = metadata.HtmlBody;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "(no preview)";
        }

        var normalized = string.Join(' ', candidate.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 140 ? normalized : normalized[..140] + "...";
    }

    private static string GetDomainName(string address)
    {
        var atIndex = address.LastIndexOf('@');
        return atIndex < 0 ? string.Empty : address[(atIndex + 1)..];
    }

    private static string SanitizePathSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(input.Length);
        foreach (var character in input)
        {
            builder.Append(invalidChars.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private sealed record MailboxPathInfo(string StorageRoot, string MessagesPath, string ProviderName);
    private sealed record LoadedMailboxMessage(StoredMailboxMessageMetadata Metadata, MailboxMessageState State);
}
