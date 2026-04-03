using System.Text.Json;

namespace InboxWeb;

public sealed class HostingDirectory
{
    private readonly Dictionary<string, HostedDomain> _domainsByName;
    private readonly Dictionary<string, List<MailboxBinding>> _mailboxesById;

    private HostingDirectory(
        IReadOnlyDictionary<string, MailStorageProviderConfiguration> storageProviders,
        Dictionary<string, HostedDomain> domainsByName,
        Dictionary<string, List<MailboxBinding>> mailboxesById)
    {
        StorageProviders = storageProviders;
        _domainsByName = domainsByName;
        _mailboxesById = mailboxesById;
    }

    public IReadOnlyDictionary<string, MailStorageProviderConfiguration> StorageProviders { get; }

    public static HostingDirectory Load(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Hosting configuration file not found: {configPath}");
        }

        using var stream = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize<SmtpHostingConfiguration>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Hosting configuration file could not be parsed.");

        var storageProviders = BuildStorageProviders(config);
        var domains = BuildDomains(config, storageProviders);
        var mailboxesById = BuildMailboxesById(domains);
        return new HostingDirectory(storageProviders, domains, mailboxesById);
    }

    public bool TryResolveRecipient(string address, out MailboxRoute route, out string rejectionResponse)
    {
        route = default!;
        rejectionResponse = string.Empty;

        var normalizedAddress = address.Trim().ToLowerInvariant();
        if (!TryGetDomain(normalizedAddress, out var domainName))
        {
            rejectionResponse = "501 5.1.7 Invalid address";
            return false;
        }

        if (!_domainsByName.TryGetValue(domainName, out var domain))
        {
            rejectionResponse = "550 5.1.2 Domain not hosted here";
            return false;
        }

        if (!domain.Mailboxes.TryGetValue(normalizedAddress, out var mailbox))
        {
            rejectionResponse = "550 5.1.1 Mailbox unavailable";
            return false;
        }

        route = new MailboxRoute(mailbox.Address, mailbox.MailboxId, domain.Name, mailbox.StorageProviderName);
        return true;
    }

    public IReadOnlyList<MailboxBinding> GetMailboxBindings(string mailboxId)
    {
        if (string.IsNullOrWhiteSpace(mailboxId))
        {
            return Array.Empty<MailboxBinding>();
        }

        return _mailboxesById.TryGetValue(mailboxId.Trim(), out var bindings)
            ? bindings
            : Array.Empty<MailboxBinding>();
    }

    private static Dictionary<string, MailStorageProviderConfiguration> BuildStorageProviders(SmtpHostingConfiguration config)
    {
        var providers = new Dictionary<string, MailStorageProviderConfiguration>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in config.StorageProviders)
        {
            if (!string.IsNullOrWhiteSpace(provider.Name))
            {
                providers[provider.Name] = provider;
            }
        }

        if (providers.Count == 0)
        {
            providers["local-default"] = MailStorageProviderConfiguration.CreateLocalDefault(null);
        }

        return providers;
    }

    private static Dictionary<string, HostedDomain> BuildDomains(
        SmtpHostingConfiguration config,
        IReadOnlyDictionary<string, MailStorageProviderConfiguration> storageProviders)
    {
        var domains = new Dictionary<string, HostedDomain>(StringComparer.OrdinalIgnoreCase);
        foreach (var domainConfig in config.Domains)
        {
            if (string.IsNullOrWhiteSpace(domainConfig.Name))
            {
                continue;
            }

            var domainName = domainConfig.Name.Trim().ToLowerInvariant();
            var defaultProvider = string.IsNullOrWhiteSpace(domainConfig.DefaultStorageProvider)
                ? "local-default"
                : domainConfig.DefaultStorageProvider.Trim();
            if (!storageProviders.ContainsKey(defaultProvider))
            {
                throw new InvalidOperationException($"Storage provider '{defaultProvider}' referenced by domain '{domainName}' is not defined.");
            }

            var mailboxes = new Dictionary<string, HostedMailbox>(StringComparer.OrdinalIgnoreCase);
            foreach (var mailbox in domainConfig.Mailboxes)
            {
                if (string.IsNullOrWhiteSpace(mailbox.Address))
                {
                    continue;
                }

                var address = mailbox.Address.Trim().ToLowerInvariant();
                if (!TryGetDomain(address, out var addressDomain) ||
                    !string.Equals(addressDomain, domainName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Mailbox '{address}' does not belong to configured domain '{domainName}'.");
                }

                var providerName = string.IsNullOrWhiteSpace(mailbox.StorageProvider)
                    ? defaultProvider
                    : mailbox.StorageProvider.Trim();
                if (!storageProviders.ContainsKey(providerName))
                {
                    throw new InvalidOperationException($"Storage provider '{providerName}' referenced by mailbox '{address}' is not defined.");
                }

                var localPart = address[..address.IndexOf('@')];
                var mailboxId = string.IsNullOrWhiteSpace(mailbox.MailboxId) ? localPart : mailbox.MailboxId.Trim();
                mailboxes[address] = new HostedMailbox(address, mailboxId, providerName);
            }

            domains[domainName] = new HostedDomain(domainName, defaultProvider, mailboxes);
        }

        return domains;
    }

    private static Dictionary<string, List<MailboxBinding>> BuildMailboxesById(IReadOnlyDictionary<string, HostedDomain> domains)
    {
        var mailboxesById = new Dictionary<string, List<MailboxBinding>>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in domains.Values)
        {
            foreach (var mailbox in domain.Mailboxes.Values)
            {
                if (!mailboxesById.TryGetValue(mailbox.MailboxId, out var bindings))
                {
                    bindings = new List<MailboxBinding>();
                    mailboxesById[mailbox.MailboxId] = bindings;
                }

                bindings.Add(new MailboxBinding(mailbox.MailboxId, mailbox.Address, domain.Name, mailbox.StorageProviderName));
            }
        }

        return mailboxesById;
    }

    private static bool TryGetDomain(string address, out string domain)
    {
        var atIndex = address.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == address.Length - 1)
        {
            domain = string.Empty;
            return false;
        }

        domain = address[(atIndex + 1)..];
        return true;
    }
}

public sealed class SmtpHostingConfiguration
{
    public List<MailStorageProviderConfiguration> StorageProviders { get; init; } = new();
    public List<HostedDomainConfiguration> Domains { get; init; } = new();
}

public sealed class MailStorageProviderConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = MailStorageProviderTypes.FileSystem;
    public FileSystemStorageOptions? FileSystem { get; set; }
    public AwsS3StorageOptions? AwsS3 { get; set; }
    public AzureFilesStorageOptions? AzureFiles { get; set; }
    public GcpStorageOptions? Gcp { get; set; }
    public SnowflakeStorageOptions? Snowflake { get; set; }

    public static MailStorageProviderConfiguration CreateLocalDefault(string? rootPath)
    {
        return new MailStorageProviderConfiguration
        {
            Name = "local-default",
            Type = MailStorageProviderTypes.FileSystem,
            FileSystem = new FileSystemStorageOptions
            {
                RootPath = string.IsNullOrWhiteSpace(rootPath)
                    ? Path.Combine(AppContext.BaseDirectory, "emails")
                    : rootPath
            }
        };
    }
}

public static class MailStorageProviderTypes
{
    public const string FileSystem = "fileSystem";
}

public sealed record FileSystemStorageOptions
{
    public string RootPath { get; init; } = string.Empty;
}

public sealed record AwsS3StorageOptions
{
    public string BucketName { get; init; } = string.Empty;
    public string? Region { get; init; }
    public string? Prefix { get; init; }
    public string? ServiceUrl { get; init; }
}

public sealed record AzureFilesStorageOptions
{
    public string ShareName { get; init; } = string.Empty;
    public string? ConnectionString { get; init; }
    public string? DirectoryPrefix { get; init; }
}

public sealed record GcpStorageOptions
{
    public string BucketName { get; init; } = string.Empty;
    public string? ProjectId { get; init; }
    public string? Prefix { get; init; }
    public string? ServiceUrl { get; init; }
}

public sealed record SnowflakeStorageOptions
{
    public string AccountIdentifier { get; init; } = string.Empty;
    public string Database { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Warehouse { get; init; } = string.Empty;
    public string TableName { get; init; } = "MAILBOX_MESSAGES";
}

public sealed class HostedDomainConfiguration
{
    public string Name { get; init; } = string.Empty;
    public string? DefaultStorageProvider { get; init; }
    public List<HostedMailboxConfiguration> Mailboxes { get; init; } = new();
}

public sealed class HostedMailboxConfiguration
{
    public string Address { get; init; } = string.Empty;
    public string? MailboxId { get; init; }
    public string? StorageProvider { get; init; }
}

public sealed record MailboxRoute(string Address, string MailboxId, string DomainName, string StorageProviderName);
public sealed record MailboxBinding(string MailboxId, string Address, string DomainName, string StorageProviderName);

internal sealed record HostedDomain(string Name, string DefaultStorageProviderName, Dictionary<string, HostedMailbox> Mailboxes);
internal sealed record HostedMailbox(string Address, string MailboxId, string StorageProviderName);
