using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class AwsS3MailboxStorageProvider : UnsupportedMailboxStorageProviderBase
{
    public AwsS3MailboxStorageProvider(string name, AwsS3StorageOptions options, ILogger<AwsS3MailboxStorageProvider> logger)
        : base(name, MailStorageProviderTypes.AwsS3, $"AWS S3 storage provider '{name}' is configured but not yet wired to an SDK or emulator. Bucket='{options.BucketName}'.", logger)
    {
    }
}

public sealed class AzureFilesMailboxStorageProvider : UnsupportedMailboxStorageProviderBase
{
    public AzureFilesMailboxStorageProvider(string name, AzureFilesStorageOptions options, ILogger<AzureFilesMailboxStorageProvider> logger)
        : base(name, MailStorageProviderTypes.AzureFiles, $"Azure Files storage provider '{name}' is configured but not yet wired to an SDK or emulator. Share='{options.ShareName}'.", logger)
    {
    }
}

public sealed class GcpMailboxStorageProvider : UnsupportedMailboxStorageProviderBase
{
    public GcpMailboxStorageProvider(string name, GcpStorageOptions options, ILogger<GcpMailboxStorageProvider> logger)
        : base(name, MailStorageProviderTypes.Gcp, $"GCP storage provider '{name}' is configured but not yet wired to an SDK or emulator. Bucket='{options.BucketName}'.", logger)
    {
    }
}

public sealed class SnowflakeMailboxStorageProvider : UnsupportedMailboxStorageProviderBase
{
    public SnowflakeMailboxStorageProvider(string name, SnowflakeStorageOptions options, ILogger<SnowflakeMailboxStorageProvider> logger)
        : base(name, MailStorageProviderTypes.Snowflake, $"Snowflake storage provider '{name}' is configured but not yet wired to a database client. Target='{options.Database}.{options.Schema}.{options.TableName}'.", logger)
    {
    }
}

public abstract class UnsupportedMailboxStorageProviderBase : IMailboxStorageProvider
{
    private readonly string _message;
    private readonly ILogger _logger;

    protected UnsupportedMailboxStorageProviderBase(string name, string type, string message, ILogger logger)
    {
        Name = name;
        Type = type;
        _message = message;
        _logger = logger;
    }

    public string Name { get; }
    public string Type { get; }

    public Task StoreAsync(MailboxStorageDelivery delivery, CancellationToken cancellationToken = default)
    {
        _logger.LogError(
            "Storage provider {ProviderName} of type {ProviderType} cannot store mail for mailbox {MailboxId}: {Message}",
            Name,
            Type,
            delivery.MailboxId,
            _message);
        throw new NotSupportedException(_message);
    }

    public Task<IReadOnlyList<MailboxMessageSummary>> ListMessagesAsync(string mailboxId, CancellationToken cancellationToken = default)
    {
        _logger.LogError(
            "Storage provider {ProviderName} of type {ProviderType} cannot list mail for mailbox {MailboxId}: {Message}",
            Name,
            Type,
            mailboxId,
            _message);
        throw new NotSupportedException(_message);
    }

    public Task<StoredMailboxMessage?> GetMessageAsync(string mailboxId, string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogError(
            "Storage provider {ProviderName} of type {ProviderType} cannot read message {MessageId} for mailbox {MailboxId}: {Message}",
            Name,
            Type,
            messageId,
            mailboxId,
            _message);
        throw new NotSupportedException(_message);
    }
}
