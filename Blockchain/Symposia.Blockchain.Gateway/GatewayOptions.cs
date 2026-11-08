namespace Symposia.Blockchain.Gateway;

/// <summary>
/// Bootstrap Chain Gateway configuration (issue #110). Points at the L3 RPC
/// endpoint and the deployed NodeRegistry/EpochRootRegistry contracts, and
/// holds the foundation-operated relayer key that sponsors gas for node
/// registration and epoch-root submission (see the Arch pass on #110,
/// "Gas bootstrapping").
/// </summary>
public sealed class GatewayOptions
{
    public string RpcUrl { get; set; } = "http://localhost:8545";
    public string NodeRegistryAddress { get; set; } = "";
    public string EpochRootRegistryAddress { get; set; } = "";

    /// <summary>The ProtocolActivations contract (issue #145); empty = no activations on this network.</summary>
    public string ProtocolActivationsAddress { get; set; } = "";

    /// <summary>
    /// Private key of the foundation-operated relayer account that pays gas
    /// for relayed transactions. The relayed node's identity comes from the
    /// EIP-712 signature it supplies, not from this account.
    /// </summary>
    public string RelayerPrivateKey { get; set; } = "";

    /// <summary>RewardDistributor (#80); empty = this network has no rewards stack and the sealer stays off.</summary>
    public string RewardDistributorAddress { get; set; } = "";

    /// <summary>EpochController (#72); required alongside <see cref="RewardDistributorAddress"/> for the sealer.</summary>
    public string EpochControllerAddress { get; set; } = "";

    public EpochSealerOptions EpochSealer { get; set; } = new();

    /// <summary>TelemetryCheckpointRegistry (#75/#158); empty = the telemetry relay endpoints answer 404.</summary>
    public string TelemetryRegistryAddress { get; set; } = "";

    /// <summary>
    /// Where the Gateway stores self-checkpoint batches it relays (#158), so nodes need no blob
    /// credential. Without an endpoint, batches are kept in <c>CacheDirectory</c> only.
    /// </summary>
    public Symposia.Network.Node.Telemetry.Committee.TelemetryBlobOptions TelemetryBlobs { get; set; } = new();

    /// <summary>StakingNodeRegistry; published to joining hosts (#172). Empty = none.</summary>
    public string StakingRegistryAddress { get; set; } = "";

    /// <summary>Block the rewards stack was deployed at, so joiners' log scans start there (#172). 0 = unknown.</summary>
    public ulong DeployBlock { get; set; }

    /// <summary>
    /// RPC URL advertised to joining hosts instead of this gateway's own <c>/v1/rpc</c> (#172).
    /// Never put a provider URL that embeds an API key here: every joiner receives it.
    /// </summary>
    public string PublicRpcUrl { get; set; } = "";

    /// <summary>This gateway's externally reachable base URL, when the request host isn't (e.g. behind a proxy).</summary>
    public string PublicBaseUrl { get; set; } = "";

    public RpcProxyOptions RpcProxy { get; set; } = new();
}

/// <summary>
/// The epoch sealer (issue #72): settles each epoch on the RewardDistributor once the
/// EpochController's finality window closes, paying gas from the relayer account. Sealing is
/// permissionless and idempotent on-chain, so running it on more than one gateway is safe.
/// </summary>
public sealed class EpochSealerOptions
{
    public bool Enabled { get; set; } = true;

    public int PollSeconds { get; set; } = 30;

    /// <summary>Most epochs settled per pass, so catching up after downtime can't starve shutdown.</summary>
    public int MaxEpochsPerPass { get; set; } = 50;

    /// <summary>First block searched for metric-report logs (the rewards deployment block).</summary>
    public ulong FromBlock { get; set; }
}
