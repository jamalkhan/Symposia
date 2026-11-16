using Prometheus;

namespace Symposia.BlobStorage.Gateway.Health;

/// <summary>
/// The gateway's Prometheus metric families (#1 FR-26), named as in
/// Requirements/BlobStorage/health-check-endpoints.md. Each app instance owns its registry, so several
/// gateways in one process (tests, the Node Host) never share counters.
/// </summary>
public sealed class GatewayMetrics
{
    private static readonly QuantileEpsilonPair[] Quantiles =
        [new(0.5, 0.05), new(0.9, 0.01), new(0.99, 0.001)];

    public GatewayMetrics()
    {
        Registry = Metrics.NewCustomRegistry();
        var f = Metrics.WithCustomRegistry(Registry);

        RequestsTotal = f.CreateCounter("gateway_requests_total", "Requests served.", "method", "status_code", "operation");
        RequestDuration = f.CreateSummary("gateway_request_duration_seconds", "Request latency.",
            new SummaryConfiguration { LabelNames = ["operation"], Objectives = Quantiles });
        RequestBytes = f.CreateCounter("gateway_request_bytes_total", "Body bytes received (in) and sent (out).", "direction");

        NodeSelectionScore = f.CreateGauge("gateway_node_selection_score", "Read-routing score per node (higher is preferred).", "node_id");
        NodeErrors = f.CreateCounter("gateway_node_errors_total", "Failed calls to storage nodes.", "node_id", "error_type");
        ReadFailovers = f.CreateCounter("gateway_read_failovers_total", "Reads moved to another replica after a node failed.");

        WriteQuorumReached = f.CreateCounter("gateway_write_quorum_reached_total", "Writes that reached quorum.");
        WriteQuorumFailed = f.CreateCounter("gateway_write_quorum_failed_total", "Writes that did not reach quorum.");
        WriteFanoutDuration = f.CreateSummary("gateway_write_fanout_duration_seconds", "Time from first byte to quorum.",
            new SummaryConfiguration { Objectives = Quantiles });

        NodeCacheSize = f.CreateGauge("gateway_node_cache_size", "Storage nodes in the health cache.");
        MetadataIndexLag = f.CreateGauge("gateway_metadata_index_lag_seconds", "Seconds since the metadata index last answered.");
        EligibleNodes = f.CreateGauge("gateway_eligible_nodes_count", "Healthy nodes that take writes.");

        // #12 Layer 2/3: the gossip-fed routing cache, its fallbacks, and epoch-root disputes.
        RoutingCacheCids = f.CreateGauge("gateway_routing_cache_cids", "CIDs in the routing cache with at least one holder.");
        RoutingCacheWarm = f.CreateGauge("gateway_routing_cache_warm", "1 once every watched node has delivered its snapshot and backlog.");
        GossipLag = f.CreateSummary("gateway_gossip_propagation_lag_seconds", "Commit-on-node to applied-in-cache delay of live manifest events.",
            new SummaryConfiguration { Objectives = Quantiles });
        RoutingLookups = f.CreateCounter("gateway_routing_lookups_total", "Replica lookups beyond the metadata index, by where they were answered.", "source");
        Disputes = f.CreateCounter("gateway_epoch_root_disputes_total", "Holdings checked against a node's on-chain manifest root, by outcome.", "outcome");
    }

    public CollectorRegistry Registry { get; }

    public Counter RequestsTotal { get; }
    public Summary RequestDuration { get; }
    public Counter RequestBytes { get; }
    public Gauge NodeSelectionScore { get; }
    public Counter NodeErrors { get; }
    public Counter ReadFailovers { get; }
    public Counter WriteQuorumReached { get; }
    public Counter WriteQuorumFailed { get; }
    public Summary WriteFanoutDuration { get; }
    public Gauge NodeCacheSize { get; }
    public Gauge MetadataIndexLag { get; }
    public Gauge EligibleNodes { get; }
    public Gauge RoutingCacheCids { get; }
    public Gauge RoutingCacheWarm { get; }
    public Summary GossipLag { get; }
    public Counter RoutingLookups { get; }
    public Counter Disputes { get; }
}
