using System.Text.Json;

namespace InboxWeb;

public sealed class HostedMailboxRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly InboxWebOptions _options;
    private readonly ILogger<HostedMailboxRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HostedMailboxRepository(InboxWebOptions options, ILogger<HostedMailboxRepository> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ListHostedDomainsAsync(CancellationToken cancellationToken = default)
    {
        var config = await LoadConfigurationAsync(cancellationToken);
        return config.Domains
            .Select(static domain => domain.Name.Trim())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<HostedMailboxDescriptor?> FindMailboxAsync(string address, CancellationToken cancellationToken = default)
    {
        var normalizedAddress = address.Trim().ToLowerInvariant();
        var directory = await LoadDirectoryAsync(cancellationToken);
        if (!directory.TryResolveRecipient(normalizedAddress, out var route, out _))
        {
            return null;
        }

        return new HostedMailboxDescriptor(route.MailboxId, route.Address, route.DomainName, route.StorageProviderName);
    }

    public async Task<HostedMailboxDescriptor> EnsureMailboxAsync(
        string username,
        string domain,
        CancellationToken cancellationToken = default)
    {
        var normalizedDomain = domain.Trim().ToLowerInvariant();
        var normalizedUsername = username.Trim().ToLowerInvariant();
        var address = $"{normalizedUsername}@{normalizedDomain}";

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var config = await LoadConfigurationAsync(cancellationToken);
            var domainConfig = config.Domains.FirstOrDefault(existingDomain =>
                string.Equals(existingDomain.Name, normalizedDomain, StringComparison.OrdinalIgnoreCase));
            if (domainConfig is null)
            {
                throw new InvalidOperationException($"Domain '{normalizedDomain}' is not hosted by this server.");
            }

            var existingMailbox = domainConfig.Mailboxes.FirstOrDefault(existingMailbox =>
                string.Equals(existingMailbox.Address, address, StringComparison.OrdinalIgnoreCase));
            if (existingMailbox is not null)
            {
                return new HostedMailboxDescriptor(
                    string.IsNullOrWhiteSpace(existingMailbox.MailboxId) ? normalizedUsername : existingMailbox.MailboxId.Trim(),
                    address,
                    normalizedDomain,
                    string.IsNullOrWhiteSpace(existingMailbox.StorageProvider)
                        ? (domainConfig.DefaultStorageProvider ?? "local-default")
                        : existingMailbox.StorageProvider.Trim());
            }

            var mailboxId = GenerateMailboxId(config, normalizedUsername);
            var updatedDomains = config.Domains
                .Select(existingDomain =>
                {
                    if (!string.Equals(existingDomain.Name, domainConfig.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return existingDomain;
                    }

                    var updatedMailboxes = existingDomain.Mailboxes
                        .Append(new HostedMailboxConfiguration
                        {
                            Address = address,
                            MailboxId = mailboxId
                        })
                        .ToList();

                    return new HostedDomainConfiguration
                    {
                        Name = existingDomain.Name,
                        DefaultStorageProvider = existingDomain.DefaultStorageProvider,
                        Mailboxes = updatedMailboxes
                    };
                })
                .ToList();

            var updatedConfig = new SmtpHostingConfiguration
            {
                StorageProviders = config.StorageProviders,
                Domains = updatedDomains
            };
            await SaveConfigurationAsync(updatedConfig, cancellationToken);

            _logger.LogInformation("Registered hosted mailbox {Address} with mailbox id {MailboxId}", address, mailboxId);
            return new HostedMailboxDescriptor(
                mailboxId,
                address,
                normalizedDomain,
                string.IsNullOrWhiteSpace(domainConfig.DefaultStorageProvider) ? "local-default" : domainConfig.DefaultStorageProvider.Trim());
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<HostingDirectory> LoadDirectoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(HostingDirectory.Load(_options.HostingConfigPath));
    }

    public async Task<IReadOnlyList<string>> ListFileSystemStorageRootsAsync(CancellationToken cancellationToken = default)
    {
        var directory = await LoadDirectoryAsync(cancellationToken);
        return directory.StorageProviders.Values
            .Where(static provider => string.Equals(provider.Type, MailStorageProviderTypes.FileSystem, StringComparison.OrdinalIgnoreCase)
                && provider.FileSystem is not null)
            .Select(static provider => provider.FileSystem!.RootPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<SmtpHostingConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.HostingConfigPath))
        {
            throw new FileNotFoundException($"Hosting configuration file not found: {_options.HostingConfigPath}");
        }

        await using var stream = File.OpenRead(_options.HostingConfigPath);
        var config = await JsonSerializer.DeserializeAsync<SmtpHostingConfiguration>(
            stream,
            SerializerOptions,
            cancellationToken);

        return config ?? throw new InvalidOperationException("Hosting configuration file could not be parsed.");
    }

    private async Task SaveConfigurationAsync(SmtpHostingConfiguration configuration, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(_options.HostingConfigPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        await File.WriteAllTextAsync(
            _options.HostingConfigPath,
            JsonSerializer.Serialize(configuration, SerializerOptions),
            cancellationToken);
    }

    private static string GenerateMailboxId(SmtpHostingConfiguration configuration, string username)
    {
        var baseValue = username
            .Replace(".", "-", StringComparison.Ordinal)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("_", "-", StringComparison.Ordinal);
        var candidate = string.IsNullOrWhiteSpace(baseValue) ? "mailbox" : baseValue;
        var counter = 1;

        var usedMailboxIds = configuration.Domains
            .SelectMany(static domain => domain.Mailboxes)
            .Select(mailbox => string.IsNullOrWhiteSpace(mailbox.MailboxId)
                ? mailbox.Address[..mailbox.Address.IndexOf('@')]
                : mailbox.MailboxId!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        while (usedMailboxIds.Contains(candidate))
        {
            candidate = $"{baseValue}-{counter}";
            counter++;
        }

        return candidate;
    }
}
