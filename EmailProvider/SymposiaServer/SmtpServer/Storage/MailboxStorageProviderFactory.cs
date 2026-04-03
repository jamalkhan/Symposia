using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public static class MailboxStorageProviderFactory
{
    public static IReadOnlyDictionary<string, IMailboxStorageProvider> CreateProviders(HostingDirectory directory, ILoggerFactory loggerFactory)
    {
        var providers = new Dictionary<string, IMailboxStorageProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerConfiguration in directory.StorageProviders.Values)
        {
            providers[providerConfiguration.Name] = CreateProvider(providerConfiguration, loggerFactory);
        }

        return providers;
    }

    private static IMailboxStorageProvider CreateProvider(MailStorageProviderConfiguration configuration, ILoggerFactory loggerFactory)
    {
        return configuration.Type switch
        {
            var type when string.Equals(type, MailStorageProviderTypes.FileSystem, StringComparison.OrdinalIgnoreCase)
                => new FileSystemMailboxStorageProvider(
                    configuration.Name,
                    configuration.FileSystem ?? new FileSystemStorageOptions(),
                    loggerFactory.CreateLogger<FileSystemMailboxStorageProvider>()),
            var type when string.Equals(type, MailStorageProviderTypes.AwsS3, StringComparison.OrdinalIgnoreCase)
                => new AwsS3MailboxStorageProvider(
                    configuration.Name,
                    configuration.AwsS3 ?? new AwsS3StorageOptions(),
                    loggerFactory.CreateLogger<AwsS3MailboxStorageProvider>()),
            var type when string.Equals(type, MailStorageProviderTypes.AzureFiles, StringComparison.OrdinalIgnoreCase)
                => new AzureFilesMailboxStorageProvider(
                    configuration.Name,
                    configuration.AzureFiles ?? new AzureFilesStorageOptions(),
                    loggerFactory.CreateLogger<AzureFilesMailboxStorageProvider>()),
            var type when string.Equals(type, MailStorageProviderTypes.Gcp, StringComparison.OrdinalIgnoreCase)
                => new GcpMailboxStorageProvider(
                    configuration.Name,
                    configuration.Gcp ?? new GcpStorageOptions(),
                    loggerFactory.CreateLogger<GcpMailboxStorageProvider>()),
            var type when string.Equals(type, MailStorageProviderTypes.Snowflake, StringComparison.OrdinalIgnoreCase)
                => new SnowflakeMailboxStorageProvider(
                    configuration.Name,
                    configuration.Snowflake ?? new SnowflakeStorageOptions(),
                    loggerFactory.CreateLogger<SnowflakeMailboxStorageProvider>()),
            _ => throw new InvalidOperationException($"Storage provider '{configuration.Name}' uses unsupported type '{configuration.Type}'.")
        };
    }
}
