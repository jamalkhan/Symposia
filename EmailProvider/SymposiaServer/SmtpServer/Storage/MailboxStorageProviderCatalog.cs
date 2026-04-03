namespace NativeSmtpReceiver;

public sealed class MailboxStorageProviderCatalog
{
    public MailboxStorageProviderCatalog(HostingDirectory hostingDirectory, ILoggerFactory loggerFactory)
    {
        Providers = MailboxStorageProviderFactory.CreateProviders(hostingDirectory, loggerFactory);
    }

    public IReadOnlyDictionary<string, IMailboxStorageProvider> Providers { get; }
}
