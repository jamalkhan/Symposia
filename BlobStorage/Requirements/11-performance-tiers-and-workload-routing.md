# Performance Tiers and Workload Routing

## Overview

Not all nodes are equal, and not all workloads have the same requirements. A home NAS on a 10 Mbps DSL line is a perfectly valid storage node for infrequently accessed archival data. A bare-metal server with NVMe drives and a 10 Gbps uplink is the right node for hot, latency-sensitive data — and is a requirement for database-grade workloads like the Postgres/Neon layer built on top of this storage system.

Performance tiers exist to match data to the hardware best suited to serve it, maximize reward earnings for high-quality nodes, and ensure that lower-quality nodes are not penalized for hardware limitations they are upfront about — they simply serve a different class of workload.

**Principle: No node is excluded. Every node contributes. Better hardware earns more.**

---

## Performance Tier Definitions

Tiers are assigned by the network based on **verified measured metrics**, not self-declaration. A node claiming Tier 1 that benchmarks at Tier 3 speeds is classified as Tier 3. Self-reported disk type is a hint, not a classification.

Tier thresholds below are illustrative starting points subject to governance adjustment as the network matures.

### Tier 1 — Database Grade

Target use case: Postgres/Neon-compatible database page storage, high-frequency random access, real-time transactional workloads.

| Metric | Minimum Threshold |
|---|---|
| Random read IOPS (4K, QD32) | ≥ 50,000 |
| Random write IOPS (4K, QD32) | ≥ 20,000 |
| Sequential read speed | ≥ 2,000 MB/s |
| Outbound bandwidth | ≥ 1,000 Mbps (1 Gbps) |
| Median TTFB | ≤ 5 ms |
| Jitter | ≤ 2 ms |
| Packet loss | ≤ 0.01% |
| Uptime (trailing 30 epochs) | ≥ 99.5% |
| Typical hardware | NVMe SSD, datacenter or high-end co-location, fiber uplink |

### Tier 2 — Hot Storage

Target use case: Frequently accessed blobs, streaming media, CDN-like access patterns, high-throughput ingest.

| Metric | Minimum Threshold |
|---|---|
| Random read IOPS (4K, QD32) | ≥ 5,000 |
| Sequential read speed | ≥ 500 MB/s |
| Outbound bandwidth | ≥ 200 Mbps |
| Median TTFB | ≤ 25 ms |
| Jitter | ≤ 10 ms |
| Packet loss | ≤ 0.1% |
| Uptime (trailing 30 epochs) | ≥ 98% |
| Typical hardware | SATA SSD, high-quality home server or VPS, cable/fiber internet |

### Tier 3 — Warm Storage

Target use case: Occasionally accessed files, backups accessed weekly or monthly, secondary replicas for hot data.

| Metric | Minimum Threshold |
|---|---|
| Random read IOPS (4K, QD32) | ≥ 500 |
| Sequential read speed | ≥ 100 MB/s |
| Outbound bandwidth | ≥ 50 Mbps |
| Median TTFB | ≤ 150 ms |
| Uptime (trailing 30 epochs) | ≥ 95% |
| Typical hardware | HDD in a NAS, mid-range home server, cable internet |

### Tier 4 — Cold / Archival Storage

Target use case: Infrequently accessed files, long-term archival, disaster recovery copies, overflow redundancy replicas.

| Metric | Minimum Threshold |
|---|---|
| Outbound bandwidth | ≥ 10 Mbps |
| Uptime (trailing 30 epochs) | ≥ 90% |
| No IOPS minimum | — |
| Typical hardware | HDD, NAS, consumer internet (DSL, slower cable) |

A node that does not meet Tier 4 minimums is not eligible to participate until it meets at least the bandwidth and uptime floor.

---

## Tier Assignment Process

1. At node registration, the node runs a standardized benchmark suite witnessed by verifier nodes. This establishes its initial tier.
2. Tier is re-evaluated at the end of every epoch based on the rolling metrics reported during that epoch.
3. Tier upgrades take effect immediately at the next epoch boundary.
4. Tier downgrades follow a **3-epoch confirmation window**: a node must measure below a tier threshold for 3 consecutive epochs before being downgraded, to prevent a single bad measurement from reclassifying a consistently good node.
5. A node's current and historical tier are recorded on-chain and visible to tenants and the placement engine.

---

## Workload Routing

The placement engine uses tier assignments to route writes and reads to appropriate nodes. Tenants may specify a minimum tier requirement on a bucket or blob; if unspecified, the system infers an appropriate tier from access patterns.

### Write Placement

When a blob is written, the placement engine selects target nodes as follows:

1. **Explicit tier requirement**: If the bucket or blob specifies a minimum tier, only nodes meeting that tier are eligible. A write that cannot be fulfilled at the requested tier fails with an explicit error (e.g., `"Insufficient Tier 1 nodes available in region eu-west"`).
2. **Inferred tier**: If no tier is specified, the system defaults to Tier 2 for new blobs. The blob may be demoted to Tier 3 or 4 over time if access patterns indicate it is cold (see Automatic Promotion and Demotion below).
3. **Mixed-tier replication**: For a blob with multiple copies (see Redundancy requirements), the primary copies are placed on nodes at or above the target tier. Overflow copies (e.g., the +1 in a different region) may be placed on lower-tier nodes — they serve as durability copies, not performance copies.

### Read Routing

Reads are routed to the **highest-tier available replica** unless the tenant specifies otherwise. Within the same tier, the replica with the lowest current TTFB and available bandwidth is preferred.

If the highest-tier replica is temporarily unavailable, the read falls back to the next available tier. The client is not made aware of the tier used unless they query blob metadata.

### Automatic Promotion and Demotion

The system tracks access frequency per blob (read count per epoch, time-since-last-read) and adjusts tier assignment automatically:

- **Promotion**: A blob that is read more than a threshold number of times per epoch and is currently on Tier 3 or 4 nodes will have a Tier 2 (or Tier 1, if the access pattern matches) replica created. The lower-tier replica is retained as a durability copy.
- **Demotion**: A blob that has not been read in more than N epochs will have its primary copy migrated from Tier 1/2 to Tier 3 or 4, reducing storage costs and freeing high-performance nodes for active data.
- Promotion and demotion thresholds are configurable per bucket and are governance parameters at the network level.
- Tenants may pin a blob to a minimum tier to prevent demotion (e.g., a database that must always be on Tier 1 regardless of access frequency).

---

## Database-Grade Workloads (Tier 1 Specifics)

The Postgres/Neon-compatible database layer built on top of this storage system has requirements that distinguish it from general blob storage:

- **Random I/O dominates**: Database page reads and writes are predominantly small (4K–64K), random access. Sequential throughput is largely irrelevant; IOPS and TTFB are everything.
- **Write durability guarantees**: A database write must be confirmed as durable before the database engine considers the transaction committed. Tier 1 nodes must acknowledge writes only after the data has reached durable storage (fdatasync or equivalent) — write caching without persistence confirmation is not acceptable.
- **Consistent low latency**: A single high-latency outlier during a transaction can block the entire database session. P99 TTFB matters as much as median TTFB for Tier 1 placement eligibility.
- **Co-location awareness**: For the database layer, the placement engine should prefer Tier 1 nodes that are geographically close to the database compute layer (low inter-node latency), even within the same region.

Tenants using the database layer must have their storage bucket or prefix pinned to Tier 1. The database layer will enforce this at the API level and will refuse to start if the backing storage does not meet Tier 1 requirements.

---

## Tier and Rewards

Tier directly influences token rewards:

- Tier 1 nodes receive a **reward multiplier** above the base rate, reflecting the higher capital and operational cost of running high-performance hardware.
- Tier 4 nodes receive the base rate or below, reflecting lower infrastructure cost.
- The exact multipliers per tier are governance parameters.
- A node's tier multiplier is applied on top of its per-factor performance score (see Node Runner Incentives & Penalties), meaning a Tier 1 node that underperforms within its tier earns less than a Tier 1 node that consistently hits its benchmarks.

| Tier | Reward Multiplier (illustrative) |
|---|---|
| Tier 1 — Database Grade | 2.5× |
| Tier 2 — Hot Storage | 1.5× |
| Tier 3 — Warm Storage | 1.0× (base) |
| Tier 4 — Cold / Archival | 0.6× |

The multiplier reflects the value the node provides to the network's most demanding workloads, not a judgment on the node operator. A Tier 4 node providing reliable archival storage is a valued participant — it just earns proportionally to the work it can do.
