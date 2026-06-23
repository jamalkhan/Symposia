# Node Selection for Reads

## Overview

When a client requests a blob that has multiple replicas across multiple nodes and regions, the gateway must decide which node to fetch from. This decision is the single most user-visible performance choice in the entire read path — it directly determines the latency and throughput that the tenant experiences. The selection algorithm must be fast (the decision itself cannot add meaningful latency), adaptive (it must respond to changing node health and load), and correct (it must never route to a node that doesn't have the blob or is in a penalty state that makes it unreliable).

---

## Selection Algorithm

Node selection uses a **scored ranking** approach. For each read request, the gateway scores all eligible nodes holding the requested blob and selects the highest-scoring node. The score is computed in microseconds from cached data — there is no per-request network call to evaluate candidates.

### Eligibility Filter (Applied First)

Before scoring, nodes are filtered to only those that are eligible to serve the request. A node is ineligible if any of the following are true:

- The node is marked Offline in the gateway's health cache.
- The node is in penalty Stage 3 or Stage 4 (see [Node Runner Incentives and Penalties](../Network/node-runner-incentives-and-penalties.md)).
- The node's replica of the requested blob is marked Degraded or Offline in blob metadata.
- The node has failed a health probe within the last 60 seconds and has not recovered.
- The node's async copy of the blob has not yet been confirmed (the blob is still being replicated to this node).

If all replicas are ineligible, the gateway returns `503 Service Unavailable` with a `Retry-After` header.

### Scoring Factors

Each eligible node is scored 0–100 using a weighted combination:

| Factor | Weight | Description |
|---|---|---|
| **Estimated latency to client** | 40% | The gateway's cached estimate of round-trip latency between the client's IP and the node. Derived from: recent latency samples for clients in the same /24 subnet, the node's reported latency profile by region, and the physical distance between client IP geolocation and node region. Lower latency = higher score. |
| **Node performance tier** | 25% | Tier 1 scores highest, Tier 4 lowest. A higher-tier node is more likely to serve the response quickly even if latency is similar. |
| **Current node load** | 20% | Nodes reporting high concurrent connection counts or high I/O queue depth are penalized. A heavily loaded node will be slower even if its baseline latency is low. Updated from gossip every 30 seconds. |
| **Recent error rate** | 10% | Nodes that have returned errors or checksum failures recently are penalized. A node with a 0% error rate in the last epoch scores maximum; a node with >1% error rate scores near zero on this factor. |
| **Penalty stage** | 5% | Stage 1 nodes are slightly penalized. Stage 2 nodes are significantly penalized (serves only as a last resort). |

**Score formula** (illustrative):
```
score = (latency_score × 0.40)
      + (tier_score × 0.25)
      + (load_score × 0.20)
      + (error_score × 0.10)
      + (penalty_score × 0.05)
```

All factors are normalized to 0–100 before weighting.

### Tiebreaking

When two nodes have scores within 5 points of each other (effectively a tie), the gateway uses **weighted random selection** between them. This prevents all gateways from routing to the same node simultaneously when multiple nodes are performing equally well, distributing load more evenly across the replica set.

---

## Regional Preference

The scoring algorithm naturally favors nodes that are geographically close to the client via the latency factor. However, an explicit regional preference layer is applied before scoring:

1. **Same region as client**: Nodes in the region matching the client's detected geography are scored first. If any same-region node scores above a minimum threshold (40/100), it is selected without evaluating cross-region nodes.
2. **Adjacent or low-latency regions**: If no same-region node meets the threshold (e.g., the region has no eligible nodes, or all are heavily loaded), the gateway evaluates all eligible nodes globally.
3. **No region preference**: If the blob has a zero-region assignment, all eligible nodes are evaluated globally from the start with no regional preference applied.

This regional preference ensures that tenants with region-pinned data get low-latency access from within their designated region while still having a healthy fallback.

---

## Latency Estimation Cache

The gateway maintains a **latency estimation table** keyed by client subnet (/24) and node ID. This table is populated from:

- **Active probes**: The gateway periodically sends lightweight HTTP pings to all nodes in its routing table and records round-trip times.
- **Request telemetry**: Every completed read request records the actual time-to-first-byte from the node. This updates the latency estimate for the client's subnet and the serving node.
- **Node-reported metrics**: Nodes self-report their average latency to recent clients by region in their gossip heartbeat. This provides a prior estimate for client subnets the gateway hasn't seen before.

Cache entries expire after 5 minutes. A node that hasn't been observed recently falls back to its region-average latency estimate.

---

## Streaming and Failover

Once a node is selected and the gateway begins streaming the response to the client, the following rules apply:

- If the node closes the connection mid-stream (before the full content-length is delivered), the gateway **immediately retries from the next-best node**, starting from the byte offset where the stream interrupted. The client receives a seamless response via chunked transfer encoding — it sees a continuous stream, not an error.
- If a checksum mismatch is detected mid-stream (the running hash diverges from the stored ETag), the gateway terminates the stream, returns `503` to the client, flags the serving node's replica for read repair, and the client must retry.
- Retry on failover is attempted at most **twice** (three nodes total). If all three fail, the gateway returns `503`.

---

## Read Routing for Range Requests

Range requests (`Range: bytes=0-1023`) follow the same node selection algorithm. The selected node receives the forwarded `Range` header and returns only the requested byte range. This is efficient — the node does not read or transmit the full blob.

For requests spanning multiple ranges (`Range: bytes=0-99, 200-299`), the gateway issues separate requests to the selected node and assembles the `multipart/byteranges` response. The node does not need to support multi-range natively; the gateway handles the assembly.

---

## Metrics and Observability

The gateway emits the following per-request metrics for monitoring and tuning:

- Node ID selected and its score.
- All candidate node scores (for debugging routing decisions).
- Whether regional preference was applied or bypassed.
- Whether a failover occurred and to which node.
- Actual TTFB from the selected node.
- Whether the request was served from cache (future: edge cache layer).

These metrics feed into the Tenant Observability dashboard (see [Tenant Observability](./tenant-observability.md)) and the platform's internal performance monitoring.
