namespace Symposia.BlobStorage.CredentialAuthority;

public enum InternalTlsMode
{
    /// <summary>No TLS on the internal API (development): the Authority warns at startup.</summary>
    Off,

    /// <summary>TLS on; a client certificate, when presented, must be allowlisted; requests without one are accepted and counted.</summary>
    Permissive,

    /// <summary>TLS on and an allowlisted client certificate required for every internal request (production).</summary>
    Enforced,
}

/// <summary>
/// Mutual TLS for the gateway-only API, <c>/internal/*</c> and Watch (#245 FR6). A mode, not a bool: the rollout is
/// Off → Permissive (deploy gateway certificates; watch <c>authority_internal_requests_without_cert_total</c> reach 0)
/// → Enforced, so no gateway is ever cut off without warning.
/// </summary>
public sealed class InternalTlsOptions
{
    /// <summary>Unset: Off in Development or when no certificate is configured (with a warning), else Permissive.</summary>
    public InternalTlsMode? Mode { get; set; }

    /// <summary>Port of the internal listener (TLS).</summary>
    public int Port { get; set; } = 5443;

    /// <summary>Port for /v1 and /health when TLS is on and Kestrel:Endpoints doesn't declare them.</summary>
    public int OperatorPort { get; set; } = 5080;

    /// <summary>The Authority's server certificate: PFX, or PEM (key in the file or <see cref="KeyPath"/>).</summary>
    public string CertificatePath { get; set; } = "";

    public string KeyPath { get; set; } = "";

    public string CertificatePassword { get; set; } = "";

    /// <summary>Lower-case hex SHA-256 fingerprints of the gateway client certificates allowed (re-read without restart).</summary>
    public string[] ClientFingerprints { get; set; } = [];
}

public sealed class AuthorityOptions
{
    public InternalTlsOptions InternalTls { get; set; } = new();

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
