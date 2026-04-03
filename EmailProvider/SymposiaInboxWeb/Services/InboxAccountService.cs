using System.Text.Json;

namespace InboxWeb;

public sealed class InboxAccountService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly InboxWebOptions _options;
    private readonly HostedMailboxRepository _mailboxRepository;
    private readonly PasswordHashingService _passwordHashingService;
    private readonly ILogger<InboxAccountService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InboxAccountService(
        InboxWebOptions options,
        HostedMailboxRepository mailboxRepository,
        PasswordHashingService passwordHashingService,
        ILogger<InboxAccountService> logger)
    {
        _options = options;
        _mailboxRepository = mailboxRepository;
        _passwordHashingService = passwordHashingService;
        _logger = logger;
        EnsureStoreExists();
    }

    public async Task<InboxAccountSession> RegisterAsync(RegisterAccountRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            throw new InvalidOperationException("Username is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Domain))
        {
            throw new InvalidOperationException("Domain is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        {
            throw new InvalidOperationException("Password must be at least 8 characters.");
        }

        var address = $"{request.Username.Trim().ToLowerInvariant()}@{request.Domain.Trim().ToLowerInvariant()}";

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadStoreAsync(cancellationToken);
            if (store.Accounts.Any(account => string.Equals(account.Address, address, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Inbox '{address}' already exists.");
            }

            var mailbox = await _mailboxRepository.EnsureMailboxAsync(request.Username, request.Domain, cancellationToken);
            var (hash, salt) = _passwordHashingService.HashPassword(request.Password);
            var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? request.Username.Trim()
                : request.DisplayName.Trim();
            var account = new InboxAccountRecord(
                Guid.NewGuid().ToString("N"),
                mailbox.Address,
                mailbox.MailboxId,
                displayName,
                hash,
                salt,
                DateTimeOffset.UtcNow,
                Array.Empty<AddressBookContactRecord>());

            store.Accounts.Add(account);
            await SaveStoreAsync(store, cancellationToken);

            _logger.LogInformation("Created inbox account {Address}", account.Address);
            return ToSession(account);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InboxAccountSession?> AuthenticateAsync(string emailAddress, string password, CancellationToken cancellationToken = default)
    {
        var store = await LoadStoreAsync(cancellationToken);
        var normalizedAddress = emailAddress.Trim().ToLowerInvariant();
        var account = store.Accounts.FirstOrDefault(existing =>
            string.Equals(existing.Address, normalizedAddress, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return null;
        }

        return _passwordHashingService.VerifyPassword(password, account.PasswordHash, account.PasswordSalt)
            ? ToSession(account)
            : null;
    }

    public async Task<InboxAccountSession?> GetAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var store = await LoadStoreAsync(cancellationToken);
        var account = store.Accounts.FirstOrDefault(existing =>
            string.Equals(existing.AccountId, accountId, StringComparison.Ordinal));
        return account is null ? null : ToSession(account);
    }

    public async Task<IReadOnlyList<AddressBookContactRecord>> ListContactsAsync(
        string accountId,
        string? query,
        CancellationToken cancellationToken = default)
    {
        var store = await LoadStoreAsync(cancellationToken);
        var account = store.Accounts.FirstOrDefault(existing =>
            string.Equals(existing.AccountId, accountId, StringComparison.Ordinal));
        if (account is null)
        {
            return Array.Empty<AddressBookContactRecord>();
        }

        return account.Contacts
            .Where(contact => string.IsNullOrWhiteSpace(query)
                || contact.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || contact.EmailAddress.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static contact => contact.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<AddressBookContactRecord> UpsertContactAsync(
        string accountId,
        ContactUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.EmailAddress))
        {
            throw new InvalidOperationException("Contact display name and email address are required.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadStoreAsync(cancellationToken);
            var accountIndex = store.Accounts.FindIndex(existing =>
                string.Equals(existing.AccountId, accountId, StringComparison.Ordinal));
            if (accountIndex < 0)
            {
                throw new InvalidOperationException("Account was not found.");
            }

            var existingAccount = store.Accounts[accountIndex];
            var contactId = string.IsNullOrWhiteSpace(request.ContactId)
                ? Guid.NewGuid().ToString("N")
                : request.ContactId.Trim();
            var updatedContact = new AddressBookContactRecord(
                contactId,
                request.DisplayName.Trim(),
                request.EmailAddress.Trim().ToLowerInvariant(),
                DateTimeOffset.UtcNow);

            var updatedContacts = existingAccount.Contacts
                .Where(existing => !string.Equals(existing.ContactId, contactId, StringComparison.Ordinal))
                .Append(updatedContact)
                .OrderBy(static contact => contact.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            store.Accounts[accountIndex] = existingAccount with { Contacts = updatedContacts };
            await SaveStoreAsync(store, cancellationToken);
            return updatedContact;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteContactAsync(string accountId, string contactId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadStoreAsync(cancellationToken);
            var accountIndex = store.Accounts.FindIndex(existing =>
                string.Equals(existing.AccountId, accountId, StringComparison.Ordinal));
            if (accountIndex < 0)
            {
                return;
            }

            var existingAccount = store.Accounts[accountIndex];
            var updatedContacts = existingAccount.Contacts
                .Where(existing => !string.Equals(existing.ContactId, contactId, StringComparison.Ordinal))
                .ToArray();
            store.Accounts[accountIndex] = existingAccount with { Contacts = updatedContacts };
            await SaveStoreAsync(store, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureStoreExists()
    {
        var directoryPath = Path.GetDirectoryName(_options.AccountStorePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        if (!File.Exists(_options.AccountStorePath))
        {
            File.WriteAllText(
                _options.AccountStorePath,
                JsonSerializer.Serialize(new InboxAccountStoreDocument(), SerializerOptions));
        }
    }

    private async Task<InboxAccountStoreDocument> LoadStoreAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_options.AccountStorePath);
        var document = await JsonSerializer.DeserializeAsync<InboxAccountStoreDocument>(
            stream,
            SerializerOptions,
            cancellationToken);

        return document ?? new InboxAccountStoreDocument();
    }

    private async Task SaveStoreAsync(InboxAccountStoreDocument document, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            _options.AccountStorePath,
            JsonSerializer.Serialize(document, SerializerOptions),
            cancellationToken);
    }

    private static InboxAccountSession ToSession(InboxAccountRecord account)
    {
        return new InboxAccountSession(account.AccountId, account.Address, account.MailboxId, account.DisplayName);
    }
}
