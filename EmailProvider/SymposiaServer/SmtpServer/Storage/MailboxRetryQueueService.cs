using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class MailboxRetryQueueService
{
    private readonly SmtpServerOptions _options;
    private readonly ILogger<MailboxRetryQueueService> _logger;

    public MailboxRetryQueueService(SmtpServerOptions options, ILogger<MailboxRetryQueueService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task EnqueueAsync(IReadOnlyList<MailboxStorageDelivery> deliveries, string reason, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(GetPendingPath());

        var item = new QueuedMailboxDelivery(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            0,
            reason,
            deliveries);
        var path = GetPendingFilePath(item.QueueId);

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        _logger.LogWarning(
            "Queued {DeliveryCount} mailbox deliveries for retry at {QueuePath}",
            deliveries.Count,
            path);
    }

    public IEnumerable<string> GetPendingFiles()
    {
        var pendingPath = GetPendingPath();
        if (!Directory.Exists(pendingPath))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(pendingPath, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<QueuedMailboxDelivery?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<QueuedMailboxDelivery>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Skipping unreadable retry queue file {QueuePath}", path);
            return null;
        }
    }

    public async Task RequeueAsync(QueuedMailboxDelivery item, string path, CancellationToken cancellationToken)
    {
        var updated = item with
        {
            AttemptCount = item.AttemptCount + 1,
            LastAttemptedAtUtc = DateTimeOffset.UtcNow
        };

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    public void Complete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeadLetter(string path)
    {
        var deadLetterPath = Path.Combine(GetDeadLetterPath(), Path.GetFileName(path));
        Directory.CreateDirectory(GetDeadLetterPath());

        if (File.Exists(deadLetterPath))
        {
            File.Delete(deadLetterPath);
        }

        File.Move(path, deadLetterPath);
    }

    private string GetPendingPath() => Path.Combine(_options.RetryQueueRootPath, "pending");
    private string GetDeadLetterPath() => Path.Combine(_options.RetryQueueRootPath, "dead-letter");
    private string GetPendingFilePath(string queueId) => Path.Combine(GetPendingPath(), $"{queueId}.json");
}

public sealed record QueuedMailboxDelivery(
    string QueueId,
    DateTimeOffset CreatedAtUtc,
    int AttemptCount,
    string Reason,
    IReadOnlyList<MailboxStorageDelivery> Deliveries)
{
    public DateTimeOffset? LastAttemptedAtUtc { get; init; }
}
