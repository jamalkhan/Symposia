namespace Symposia.BlobStorage.CredentialAuthority;

public sealed class AuthorityOptions
{
    /// <summary>Postgres connection string: the durable, shared source of truth for tenants and credentials (#3).</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Platform-operator token for tenant provisioning (#3 FR2.4). Empty disables those endpoints.</summary>
    public string OperatorToken { get; set; } = "";

    /// <summary>Token gateways present on /internal/* and Watch. Gateways are the only callers of the data paths.</summary>
    public string ServiceToken { get; set; } = "";

    /// <summary>Base64 AES-256 key encrypting credential secrets at rest.</summary>
    public string MasterKey { get; set; } = "";

    /// <summary>How long a revoked and expired credential is kept before pruning (#16 FR6.1).</summary>
    public int RevocationHoldingDays { get; set; } = 90;

    /// <summary>How long a deleted tenant is recoverable before it may be purged (#3 FR2.7).</summary>
    public int TenantRecoveryDays { get; set; } = 30;

    /// <summary>How long change rows are kept for deltas (#245); gateways further behind take the snapshot.</summary>
    public int ChangeLogRetentionDays { get; set; } = 7;

    /// <summary>A delta naming more entities than this is refused (410) in favour of the snapshot (#245).</summary>
    public int MaxDeltaEntities { get; set; } = 5000;

    /// <summary>How often a Watch stream checks for a new state version (milliseconds).</summary>
    public int WatchPollMilliseconds { get; set; } = 500;
}
