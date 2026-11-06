namespace Symposia.NodeHost.ServiceCatalog;

/// <summary>One scoring factor of a Node Service: its share of the service's score.</summary>
public sealed record ScoringFactor(string Name, int WeightBps, bool LowerIsBetter = false);

/// <summary>
/// The public earnings breakdown of one Node Service (issue #159): what it earns, from which
/// pool, how it is scored, what it must stake and what can go wrong. Shown on the Node Services
/// tab so operators can see what they are turning on and what they get in return.
/// </summary>
public sealed record NodeServiceInfo(
    int Id,
    string Key,
    string Name,
    string Description,
    bool Earns,
    string Status,
    string Funding,
    string PoolShare,
    IReadOnlyList<ScoringFactor> Factors,
    string MinimumStake,
    IReadOnlyList<string> Penalties,
    string? SettingsSection = null,
    string? Note = null);

/// <summary>
/// Every defined Node Service. A Node runs 0, 1 or many of them; each is staked, scored,
/// penalized and paid on its own. <see cref="NodeServiceInfo.Id"/> is the on-chain service
/// ordinal (<c>INodeRegistry.NodeType</c>). Pool and stake figures mirror
/// <c>Blockchain/bootstrap-chain/script/DeployRewards.s.sol</c> and tokenomics-mvp.md; the
/// catalog tests pin them so they can't drift silently.
/// </summary>
public static class NodeServiceCatalog
{
    public const string Live = "Live";
    public const string Strawman = "Strawman — subject to governance";
    public const string NotDefined = "Earnings not yet defined";
    public const string Pinned = "Pinned — coming after Blob Storage";
    public const string NeverEarns = "Never earns";

    /// <summary>Penalties every staked service is exposed to (node-level, tokenomics §9).</summary>
    private static readonly string[] NodeLevel =
    [
        "Node-level violations (overcommitment, region-verification fraud) slash every service pro rata and ban the node",
        "Hardware-fault stages are per service: Stage 1 pays that service 70%, Stage 2 40%, Stages 3-4 nothing; Stage 3-4 slash and Stage 4 bans only that service",
        "Rewards held in reserve while heartbeat compliance is at or below 90%; released after 2 compliant epochs",
    ];

    public static IReadOnlyList<NodeServiceInfo> All { get; } =
    [
        new(0, "storage", "Storage",
            "Stores and serves encrypted blob shards for the network.",
            Earns: true, Live,
            Funding: "92% capacity emission, Storage type pool",
            PoolShare: "90% of the capacity slice in the MVP deploy (long-term base weight 36%)",
            Factors:
            [
                new("Retrieval speed (ms)", 3_000, true),
                new("Uptime", 2_000),
                new("Latency / TTFB (ms)", 1_500, true),
                new("I/O throughput", 1_000),
                new("Network bandwidth", 1_000),
                new("Available storage", 800),
                new("Used storage", 700),
            ],
            MinimumStake: "25,000 SYM + 2,500 SYM per TB offered",
            Penalties:
            [
                "Storage penalty machine: Stage 3 slashes the Storage stake 5%/epoch (25% cap); Stage 4 slashes and force-deregisters Storage only",
                .. NodeLevel,
            ],
            SettingsSection: "Storage"),
        new(8, "blob-gateway", "Blob Gateway",
            "S3-compatible front door: authenticates tenants, writes to Storage services to quorum, repairs copies and serves reads.",
            Earns: true, Strawman,
            Funding: "92% capacity emission, Blob Gateway type pool",
            PoolShare: "10% of the capacity slice in the MVP deploy (long-term base weight 4%)",
            Factors:
            [
                new("Uptime", 3_000),
                new("Request success rate", 3_000),
                new("p95 latency (ms)", 2_000, true),
                new("Bytes served", 2_000),
            ],
            MinimumStake: "10,000 SYM flat",
            Penalties:
            [
                "No data-loss stages (a gateway holds no data); failed requests lower its score instead",
                .. NodeLevel,
            ],
            SettingsSection: "BlobGateway"),
        new(5, "verifier", "Verifier",
            "Checks other nodes' claims (latency, retrieval, region) and attests to them on-chain.",
            Earns: true, Live,
            Funding: "8% verifier emission",
            PoolShare: "100% of the 8% verifier slice, split by score",
            Factors:
            [
                new("Uptime", 4_000),
                new("Coverage (assigned checks completed)", 3_000),
                new("Accuracy (attestations upheld)", 3_000),
            ],
            MinimumStake: "125,000 SYM (5× the Storage base)",
            Penalties: ["Verification fraud is a node-level violation: slashed and banned", .. NodeLevel],
            Note: "Not available in NodeHost yet."),
        new(9, "blockchain-gateway", "Blockchain Gateway",
            "Relays registrations, metric reports, telemetry and epoch seals to the chain and pays their gas.",
            Earns: false, NotDefined,
            Funding: "—", PoolShare: "—", Factors: [],
            MinimumStake: "Not set",
            Penalties: [.. NodeLevel],
            Note: "Run by the foundation today. NodeHost starts one automatically in Local dev and Remote RPC modes."),
        new(10, "telemetry-committee", "Telemetry Committee",
            "Checkpoints heartbeat telemetry each hour and aggregates per-epoch records on-chain.",
            Earns: false, NotDefined,
            Funding: "—", PoolShare: "—", Factors: [],
            MinimumStake: "Not set",
            Penalties: [.. NodeLevel],
            Note: "Members are appointed by governance today."),
        new(6, "email-ip", "Email IP Address",
            "Provides a clean sending IP address for outbound email.",
            Earns: false, NeverEarns,
            Funding: "None: hardcoded 0% of emission",
            PoolShare: "0%", Factors: [],
            MinimumStake: "0 SYM (an abuse bond may be added later)",
            Penalties: ["Abuse handling is outside the reward system"],
            Note: "Not available in NodeHost yet."),
        new(7, "compute", "Compute",
            "Postgres-as-a-service compute (pageserver, safekeeper, postgres).",
            Earns: true, Pinned,
            Funding: "Fee-funded compute pool (tenant billing), not emission",
            PoolShare: "To be decided", Factors: [],
            MinimumStake: "40,000 SYM + 5,000 SYM per vCPU (placeholder)",
            Penalties: ["Compute penalty stages hit the Compute service only", .. NodeLevel],
            SettingsSection: "Compute",
            Note: "Pinned while we focus on Blob Storage (#162)."),
        new(1, "oltp", "OLTP", "Martech transactional database layer.",
            Earns: true, Pinned, "92% capacity emission, OLTP type pool", "Base weight 22% (not yet enabled)", [],
            "75,000 SYM + 15,000 SYM per size step", [.. NodeLevel], Note: "Pinned (#162)."),
        new(2, "analytics", "Analytics", "Report and query tier.",
            Earns: true, Pinned, "92% capacity emission, Analytics type pool", "Base weight 13% (not yet enabled)", [],
            "60,000 SYM + 10,000 SYM per 32 GB RAM", [.. NodeLevel], Note: "Pinned (#162)."),
        new(3, "consensus", "Consensus", "Chain security and participation.",
            Earns: true, Pinned, "92% capacity emission, Consensus type pool", "Base weight 13% (not yet enabled)", [],
            "150,000 SYM flat", [.. NodeLevel], Note: "Pinned (#162)."),
        new(4, "serverless", "Serverless", "Event-driven functions, webhooks and Journey side effects.",
            Earns: true, Pinned, "92% capacity emission, Serverless type pool", "Base weight 12% (not yet enabled)", [],
            "50,000 SYM + 5,000 SYM per invoke slot", [.. NodeLevel], Note: "Pinned (#162)."),
    ];
}
