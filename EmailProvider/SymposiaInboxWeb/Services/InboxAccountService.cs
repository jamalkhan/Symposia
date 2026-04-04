using System.Security.Cryptography;
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
        ValidatePassword(request.Password);

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
            var account = new InboxAccountRecord(
                Guid.NewGuid().ToString("N"),
                mailbox.Address,
                mailbox.MailboxId,
                string.IsNullOrWhiteSpace(request.DisplayName) ? request.Username.Trim() : request.DisplayName.Trim(),
                hash,
                salt,
                DateTimeOffset.UtcNow,
                Array.Empty<AddressBookContactRecord>(),
                CreateDefaultSecurityState());

            store.Accounts.Add(account);
            await SaveStoreAsync(store, cancellationToken);

            _logger.LogInformation("Created inbox account {Address}", account.Address);
            return ToSession(account, GenerateCsrfToken());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InboxAuthenticationResult> AuthenticateAsync(string emailAddress, string password, CancellationToken cancellationToken = default)
    {
        var normalizedAddress = emailAddress.Trim().ToLowerInvariant();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadStoreAsync(cancellationToken);
            var index = store.Accounts.FindIndex(existing =>
                string.Equals(existing.Address, normalizedAddress, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                _logger.LogWarning("Failed login attempt for unknown account {Address}", normalizedAddress);
                return new InboxAuthenticationResult(false, false, "Email address or password is incorrect.", null);
            }

            var account = store.Accounts[index];
            if (account.SecurityState.LockoutUntilUtc is { } lockoutUntil && lockoutUntil > DateTimeOffset.UtcNow)
            {
                _logger.LogWarning("Locked-out login attempt for account {Address}", account.Address);
                return new InboxAuthenticationResult(false, true, $"Account is locked until {lockoutUntil:u}.", null);
            }

            if (!_passwordHashingService.VerifyPassword(password, account.PasswordHash, account.PasswordSalt))
            {
                var failedCount = account.SecurityState.FailedLoginCount + 1;
                var lockout = failedCount >= _options.LockoutThreshold
                    ? DateTimeOffset.UtcNow.AddMinutes(_options.LockoutMinutes)
                    : account.SecurityState.LockoutUntilUtc;
                store.Accounts[index] = account with
                {
                    SecurityState = account.SecurityState with
                    {
                        FailedLoginCount = failedCount,
                        LastFailedLoginAtUtc = DateTimeOffset.UtcNow,
                        LockoutUntilUtc = lockout
                    }
                };
                await SaveStoreAsync(store, cancellationToken);

                if (lockout is not null && lockout > DateTimeOffset.UtcNow)
                {
                    _logger.LogWarning("Account {Address} has been locked out after repeated failed logins", account.Address);
                    return new InboxAuthenticationResult(false, true, $"Account is locked until {lockout:u}.", null);
                }

                _logger.LogWarning("Failed login attempt for account {Address}", account.Address);
                return new InboxAuthenticationResult(false, false, "Email address or password is incorrect.", null);
            }

            store.Accounts[index] = account with
            {
                SecurityState = account.SecurityState with
                {
                    FailedLoginCount = 0,
                    LockoutUntilUtc = null,
                    LastLoginAtUtc = DateTimeOffset.UtcNow
                }
            };
            await SaveStoreAsync(store, cancellationToken);

            _logger.LogInformation("Successful login for account {Address}", account.Address);
            return new InboxAuthenticationResult(true, false, null, ToSession(store.Accounts[index], GenerateCsrfToken()));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InboxAccountSession?> GetAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var store = await LoadStoreAsync(cancellationToken);
        var account = store.Accounts.FirstOrDefault(existing => string.Equals(existing.AccountId, accountId, StringComparison.Ordinal));
        return account is null ? null : ToSession(account, GenerateCsrfToken());
    }

    public async Task<IReadOnlyList<AddressBookContactRecord>> ListContactsAsync(string accountId, string? query, CancellationToken cancellationToken = default)
    {
        var account = await FindAccountAsync(accountId, cancellationToken);
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

    public async Task<AddressBookContactRecord> UpsertContactAsync(string accountId, ContactUpsertRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.EmailAddress))
        {
            throw new InvalidOperationException("Contact display name and email address are required.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadStoreAsync(cancellationToken);
            var accountIndex = store.Accounts.FindIndex(existing => string.Equals(existing.AccountId, accountId, StringComparison.Ordinal));
            if (accountIndex < 0)
            {
                throw new InvalidOperationException("Account was not found.");
            }

            var existingAccount = store.Accounts[accountIndex];
            var contactId = string.IsNullOrWhiteSpace(request.ContactId) ? Guid.NewGuid().ToString("N") : request.ContactId.Trim();
            var updatedContact = new AddressBookContactRecord(contactId, request.DisplayName.Trim(), request.EmailAddress.Trim().ToLowerInvariant(), DateTimeOffset.UtcNow);

            store.Accounts[accountIndex] = existingAccount with
            {
                Contacts = existingAccount.Contacts
                    .Where(existing => !string.Equals(existing.ContactId, contactId, StringComparison.Ordinal))
                    .Append(updatedContact)
                    .OrderBy(static contact => contact.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };

            await SaveStoreAsync(store, cancellationToken);
            _logger.LogInformation("Saved contact {ContactId} for account {AccountId}", contactId, accountId);
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
            var accountIndex = store.Accounts.FindIndex(existing => string.Equals(existing.AccountId, accountId, StringComparison.Ordinal));
            if (accountIndex < 0)
            {
                return;
            }

            var account = store.Accounts[accountIndex];
            store.Accounts[accountIndex] = account with
            {
                Contacts = account.Contacts
                    .Where(existing => !string.Equals(existing.ContactId, contactId, StringComparison.Ordinal))
                    .ToArray()
            };
            await SaveStoreAsync(store, cancellationToken);
            _logger.LogInformation("Deleted contact {ContactId} for account {AccountId}", contactId, accountId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PasswordResetResponse> RequestPasswordResetAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        var normalizedAddress = emailAddress.Trim().ToLowerInvariant();
        var resetToken = GenerateResetToken();
        var tokenHash = _passwordHashingService.HashSecret(resetToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadStoreAsync(cancellationToken);
            var index = store.Accounts.FindIndex(existing => string.Equals(existing.Address, normalizedAddress, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                _logger.LogInformation("Password reset requested for unknown account {Address}", normalizedAddress);
                return new PasswordResetResponse(true, _options.ExposeResetTokens ? null : null);
            }

            var account = store.Accounts[index];
            store.Accounts[index] = account with
            {
                SecurityState = account.SecurityState with
                {
                    PasswordResetTokenHash = tokenHash,
                    PasswordResetRequestedAtUtc = DateTimeOffset.UtcNow
                }
            };
            await SaveStoreAsync(store, cancellationToken);
            _logger.LogInformation("Password reset requested for account {Address}", account.Address);
            return new PasswordResetResponse(true, _options.ExposeResetTokens ? resetToken : null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken = default)
    {
        ValidatePassword(newPassword);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadStoreAsync(cancellationToken);
            var index = store.Accounts.FindIndex(existing =>
                _passwordHashingService.VerifySecret(token, existing.SecurityState.PasswordResetTokenHash));

            if (index < 0)
            {
                throw new InvalidOperationException("Password reset token is invalid.");
            }

            var account = store.Accounts[index];
            if (account.SecurityState.PasswordResetRequestedAtUtc is null ||
                account.SecurityState.PasswordResetRequestedAtUtc.Value.AddMinutes(_options.PasswordResetTokenMinutes) < DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("Password reset token has expired.");
            }

            var (hash, salt) = _passwordHashingService.HashPassword(newPassword);
            store.Accounts[index] = account with
            {
                PasswordHash = hash,
                PasswordSalt = salt,
                SecurityState = account.SecurityState with
                {
                    FailedLoginCount = 0,
                    LockoutUntilUtc = null,
                    PasswordResetTokenHash = null,
                    PasswordResetRequestedAtUtc = null,
                    LastPasswordChangedAtUtc = DateTimeOffset.UtcNow
                }
            };
            await SaveStoreAsync(store, cancellationToken);
            _logger.LogInformation("Password reset completed for account {Address}", account.Address);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<InboxAccountRecord?> FindAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        var store = await LoadStoreAsync(cancellationToken);
        return store.Accounts.FirstOrDefault(existing => string.Equals(existing.AccountId, accountId, StringComparison.Ordinal));
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
            File.WriteAllText(_options.AccountStorePath, JsonSerializer.Serialize(new InboxAccountStoreDocument(), SerializerOptions));
        }
    }

    private async Task<InboxAccountStoreDocument> LoadStoreAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_options.AccountStorePath);
        var document = await JsonSerializer.DeserializeAsync<InboxAccountStoreDocument>(stream, SerializerOptions, cancellationToken);
        return document ?? new InboxAccountStoreDocument();
    }

    private async Task SaveStoreAsync(InboxAccountStoreDocument document, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(_options.AccountStorePath, JsonSerializer.Serialize(document, SerializerOptions), cancellationToken);
    }

    private InboxAccountSecurityState CreateDefaultSecurityState()
    {
        return new InboxAccountSecurityState(0, null, null, null, null, null, null);
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new InvalidOperationException("Password must be at least 8 characters.");
        }
    }

    private static string GenerateCsrfToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static string GenerateResetToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    }

    private static InboxAccountSession ToSession(InboxAccountRecord account, string csrfToken)
    {
        return new InboxAccountSession(account.AccountId, account.Address, account.MailboxId, account.DisplayName, csrfToken);
    }
}
