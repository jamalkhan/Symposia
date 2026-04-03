using System.Text.Json;

namespace NativeSmtpReceiver;

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
    public int DomainCount => _domainsByName.Count;
    public int StorageProviderCount => StorageProviders.Count;

    public static HostingDirectory LoadFromEnvironment()
    {
        return Load(ResolveConfigPath());
    }

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

        var storageProviders = BuildStorageProviders(config, configPath);
        var domains = BuildDomains(config, storageProviders);
        var mailboxesById = BuildMailboxesById(domains);
        return new HostingDirectory(storageProviders, domains, mailboxesById);
    }

    public bool TryResolveRecipient(string address, out MailboxRoute route, out string rejectionResponse)
    {
        route = default!;

        if (!TryGetDomain(address, out var domainName))
        {
            rejectionResponse = "501 5.1.7 Invalid address";
            return false;
        }

        if (!_domainsByName.TryGetValue(domainName, out var domain))
        {
            rejectionResponse = "550 5.1.2 Domain not hosted here";
            return false;
        }

        if (!domain.Mailboxes.TryGetValue(address, out var mailbox))
        {
            rejectionResponse = "550 5.1.1 Mailbox unavailable";
            return false;
        }

        route = new MailboxRoute(
            mailbox.Address,
            mailbox.MailboxId,
            domain.Name,
            mailbox.StorageProviderName);

        rejectionResponse = string.Empty;
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

    public IReadOnlyList<HostedDomainSnapshot> GetDomains()
    {
        return _domainsByName.Values
            .OrderBy(static domain => domain.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static domain => new HostedDomainSnapshot(
                domain.Name,
                domain.DefaultStorageProviderName,
                domain.Mailboxes.Values
                    .OrderBy(static mailbox => mailbox.Address, StringComparer.OrdinalIgnoreCase)
                    .Select(mailbox => new MailboxBinding(
                        mailbox.MailboxId,
                        mailbox.Address,
                        domain.Name,
                        mailbox.StorageProviderName))
                    .ToArray()))
            .ToArray();
    }

    public IReadOnlyList<string> GetMailboxIds()
    {
        return _mailboxesById.Keys
            .OrderBy(static mailboxId => mailboxId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveConfigPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_HOSTING_CONFIG");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_RECIPIENT_CONFIG");
        }

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(AppContext.BaseDirectory, "Config", "mailboxes.json");
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, Environment.CurrentDirectory);
    }

    private static Dictionary<string, MailStorageProviderConfiguration> BuildStorageProviders(
        SmtpHostingConfiguration config,
        string configPath)
    {
        var providers = new Dictionary<string, MailStorageProviderConfiguration>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in config.StorageProviders)
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                continue;
            }

            provider.Normalize(configPath);
            providers[provider.Name] = provider;
        }

        if (providers.Count == 0)
        {
            var fallbackRoot = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_MAIL_ROOT");
            providers["local-default"] = MailStorageProviderConfiguration.CreateLocalDefault(fallbackRoot);
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

            var domainName = domainConfig.Name.Trim();
            var defaultStorageProvider = ResolveStorageProviderName(domainConfig.DefaultStorageProvider, "local-default", storageProviders, $"domain '{domainName}'");
            var mailboxes = new Dictionary<string, HostedMailbox>(StringComparer.OrdinalIgnoreCase);

            foreach (var mailboxConfig in domainConfig.Mailboxes)
            {
                if (string.IsNullOrWhiteSpace(mailboxConfig.Address))
                {
                    continue;
                }

                var address = mailboxConfig.Address.Trim();
                if (!TryGetDomain(address, out var addressDomain) ||
                    !string.Equals(addressDomain, domainName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Mailbox '{address}' does not belong to configured domain '{domainName}'.");
                }

                var localPart = address[..address.LastIndexOf('@')];
                var mailboxId = string.IsNullOrWhiteSpace(mailboxConfig.MailboxId)
                    ? localPart
                    : mailboxConfig.MailboxId.Trim();
                var storageProviderName = ResolveStorageProviderName(
                    mailboxConfig.StorageProvider,
                    defaultStorageProvider,
                    storageProviders,
                    $"mailbox '{address}'");

                mailboxes[address] = new HostedMailbox(address, mailboxId, storageProviderName);
            }

            domains[domainName] = new HostedDomain(domainName, defaultStorageProvider, mailboxes);
        }

        return domains;
    }

    private static Dictionary<string, List<MailboxBinding>> BuildMailboxesById(
        IReadOnlyDictionary<string, HostedDomain> domains)
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

                bindings.Add(new MailboxBinding(
                    mailbox.MailboxId,
                    mailbox.Address,
                    domain.Name,
                    mailbox.StorageProviderName));
            }
        }

        return mailboxesById;
    }

    private static string ResolveStorageProviderName(
        string? configuredProviderName,
        string fallbackProviderName,
        IReadOnlyDictionary<string, MailStorageProviderConfiguration> storageProviders,
        string owner)
    {
        var providerName = string.IsNullOrWhiteSpace(configuredProviderName)
            ? fallbackProviderName
            : configuredProviderName.Trim();

        if (!storageProviders.ContainsKey(providerName))
        {
            throw new InvalidOperationException($"Storage provider '{providerName}' referenced by {owner} is not defined.");
        }

        return providerName;
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

    public void Normalize(string configPath)
    {
        if (string.Equals(Type, MailStorageProviderTypes.FileSystem, StringComparison.OrdinalIgnoreCase) &&
            FileSystem is not null &&
            !string.IsNullOrWhiteSpace(FileSystem.RootPath) &&
            !Path.IsPathRooted(FileSystem.RootPath))
        {
            FileSystem = FileSystem with
            {
                RootPath = Path.GetFullPath(FileSystem.RootPath, Environment.CurrentDirectory)
            };
        }
    }
}

public static class MailStorageProviderTypes
{
    public const string FileSystem = "fileSystem";
    public const string AwsS3 = "awsS3";
    public const string AzureFiles = "azureFiles";
    public const string Gcp = "gcp";
    public const string Snowflake = "snowflake";
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
public sealed record HostedDomainSnapshot(string Name, string DefaultStorageProviderName, IReadOnlyList<MailboxBinding> Mailboxes);

internal sealed record HostedDomain(string Name, string DefaultStorageProviderName, Dictionary<string, HostedMailbox> Mailboxes);
internal sealed record HostedMailbox(string Address, string MailboxId, string StorageProviderName);
