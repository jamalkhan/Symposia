# Compute Nodes

## Overview

Compute nodes are a new participant class in the Symposia network. They provide Postgres execution capacity — CPU, RAM, and local page cache — to tenant databases. Like storage node operators, compute node operators stake tokens, are measured on performance, and earn epoch-based rewards proportional to their contribution. The same trust and incentive model applies; the hardware profile and performance metrics are different.

A compute node runs the Postgres process, the local pageserver (hot page cache), and the safekeeper process for WAL durability (see [Postgres Architecture](./postgres-architecture.md)). These three processes run on the same physical or virtual machine.

---

## Hardware Profile

Compute nodes are CPU- and RAM-intensive. Unlike storage nodes, they do not need large amounts of raw disk capacity — their local disk is used only for the pageserver cache (hot pages) and WAL safekeeper journaling, not for cold data storage (which lives in Tier 1 blob storage).

### Minimum Requirements (Any Compute Tier)

| Resource | Minimum |
|---|---|
| CPU | 4 physical cores (x86-64 or ARM64), no shared/burstable vCPUs |
| RAM | 8 GB dedicated (not swappable; swap degrades query latency critically) |
| Local disk (SSD only) | 200 GB NVMe or SATA SSD for pageserver cache and WAL journal |
| HDD or network-attached storage | Not permitted for pageserver cache or WAL |
| Outbound network | ≥ 200 Mbps sustained |
| Inbound network | ≥ 200 Mbps sustained |
| Public IP | Required (the proxy tier must be able to reach this node) |

A compute node running on a shared VPS with burstable CPU credits (e.g., AWS t-series, Azure B-series) does not meet the minimum requirements. CPU must be consistently available, not burst-on-demand.

---

## Performance Tiers

Compute tiers are determined by verified measured performance, not self-declaration. Benchmarks are run at registration by verifier nodes and re-verified periodically.

### Compute Tier 1 — High Performance

Target use case: Production OLTP workloads, high-concurrency databases, low-latency requirement.

| Metric | Minimum Threshold |
|---|---|
| CPU | ≥ 16 physical cores |
| Integer operations/sec (single-thread) | ≥ 2,000 MIPS (verified benchmark) |
| RAM | ≥ 64 GB |
| RAM bandwidth | ≥ 50 GB/s |
| Local disk (pageserver cache) | NVMe, ≥ 1 TB, ≥ 200K IOPS random read |
| Network latency to peer compute nodes in same region | ≤ 2ms RTT |
| Outbound network | ≥ 1 Gbps |
| Typical hardware | Bare metal or high-memory dedicated cloud instance |

### Compute Tier 2 — Standard

Target use case: Development, staging, moderate-traffic production, small datasets.

| Metric | Minimum Threshold |
|---|---|
| CPU | ≥ 8 physical cores |
| Integer operations/sec (single-thread) | ≥ 1,000 MIPS |
| RAM | ≥ 32 GB |
| Local disk (pageserver cache) | NVMe or SATA SSD, ≥ 500 GB, ≥ 50K IOPS random read |
| Network latency to peer compute nodes in same region | ≤ 5ms RTT |
| Outbound network | ≥ 500 Mbps |

### Compute Tier 3 — Entry

Target use case: Dev/test, personal projects, very low traffic, sleep-eligible databases.

| Metric | Minimum Threshold |
|---|---|
| CPU | ≥ 4 physical cores |
| Integer operations/sec (single-thread) | ≥ 500 MIPS |
| RAM | ≥ 8 GB |
| Local disk (pageserver cache) | SATA SSD, ≥ 200 GB |
| Network latency to peer compute nodes | ≤ 10ms RTT |
| Outbound network | ≥ 200 Mbps |

Tier 3 nodes are not eligible to host databases with HIPAA designation or SLA-backed availability guarantees. Tenants choosing Tier 3 compute receive a lower SLA.

---

## Operational Metrics

Compute node operators export metrics continuously (same 5-minute cadence as storage nodes). Metrics feed into reward calculations, tier assignment, and the proxy layer's routing decisions.

### CPU and Compute Metrics

| Metric | Description |
|---|---|
| **Active vCPU-seconds** | vCPU-seconds consumed by Postgres processes during the sampling window. Primary billing metric for tenants; also the primary reward metric for operators. |
| **Integer ops/sec** | Integer operations per second on a standard benchmark (measured at registration and periodically by verifiers). Determines tier. |
| **CPU utilization %** | Average utilization across all cores. Nodes above 85% average utilization are deprioritized for new database placements. |
| **Postgres connection count** | Active and idle connections across all hosted databases. |
| **Query latency P50 / P99** | Median and 99th percentile query execution time, sampled across all hosted databases (without exposing query content). |

### Memory Metrics

| Metric | Description |
|---|---|
| **RAM available for databases** | Total RAM minus OS and system overhead, available for Postgres processes and pageserver cache. |
| **Pageserver cache hit rate** | Percentage of page reads served from local cache vs. fetched from blob storage. High cache hit rate means lower query latency and lower blob storage egress cost. |
| **Memory pressure events** | Count of times the OS OOM killer was invoked or the node entered swap. Any memory pressure event is a Tier 1 disqualifier. |

### Network Metrics

| Metric | Description |
|---|---|
| **Intra-region latency to peers** | RTT to co-located safekeeper peers. Must remain ≤ tier threshold. If this degrades, WAL commit latency rises and the node is flagged. |
| **Latency to Tier 1 blob storage** | RTT to the Tier 1 blob nodes storing this node's databases' page data. Should be ≤ 10ms for acceptable cold-page fetch latency. |
| **Outbound bandwidth** | Measured bandwidth available for serving query results to the proxy layer. |

### Availability Metrics

| Metric | Description |
|---|---|
| **Uptime %** | Percentage of epoch the node was reachable and serving Postgres connections. |
| **Heartbeat compliance %** | Sub-epoch heartbeat submission compliance (same auto-payout threshold as storage nodes: >90%). |
| **Database restart events** | Count of unplanned Postgres process restarts. A high restart rate is a Stage 1 trigger. |
| **WAL safekeeper lag** | How far behind (in bytes) this node's safekeeper is from the primary WAL stream. High lag means durability is at risk. |

---

## Reward Calculation

Compute node rewards follow the same epoch-based system as storage nodes (see [Node Runner Incentives and Penalties](../Network/node-runner-incentives-and-penalties.md)), but the reward factors are different:

| Factor | Weight | Description |
|---|---|---|
| **Active compute delivered** | 40% | vCPU-seconds actually consumed serving tenant queries during the epoch. Nodes that actually serve workload earn more. |
| **Query latency** | 25% | P99 query latency relative to other nodes at the same tier. Lower is better. |
| **Uptime and availability** | 20% | Percentage of epoch the node was available and ready to serve connections. |
| **Pageserver cache efficiency** | 10% | Cache hit rate — reflects how well the node serves hot data without going to blob storage. |
| **WAL safekeeper reliability** | 5% | How reliably the node served its safekeeper role for peer databases during the epoch. |

**Tier multipliers** apply on top of the weighted score, identical in structure to storage node tier multipliers. Exact multipliers are governance parameters; illustratively:

| Compute Tier | Reward Multiplier |
|---|---|
| Compute Tier 1 | 3.0× |
| Compute Tier 2 | 1.5× |
| Compute Tier 3 | 0.8× |

### Reward Pool

Compute node rewards come from a separate **compute reward pool** funded from tenant compute billing revenue. Unlike storage node rewards (funded from token emission), compute rewards are funded by actual compute usage fees paid by tenants. The split between what goes to compute node operators and what the platform retains is a governance parameter.

This distinction matters: storage node rewards are emission-based (inflationary), while compute rewards are fee-based (sustainable without inflation). As the network matures and storage emission decreases, the compute reward model is more self-sustaining.

---

## Penalty Stages

Compute nodes use the same 4-stage progressive penalty system as storage nodes (see [Node Runner Incentives and Penalties](../Network/node-runner-incentives-and-penalties.md)), adapted for compute-specific triggers:

| Stage | Trigger | Effect |
|---|---|---|
| **Stage 1 — Warning** | Elevated P99 query latency, rising restart rate, or WAL safekeeper lag > 5 minutes for 2+ consecutive epochs | 70% reward multiplier; no stake touched; operator alerted |
| **Stage 2 — Degraded** | Stage 1 conditions persist 2+ epochs, or memory pressure events detected | 40% reward multiplier; databases on this node become candidates for live migration to peer compute nodes |
| **Stage 3 — Suspended** | Stage 2 conditions persist, or a database becomes unavailable due to compute node failure | 0% rewards; stake slashing begins at 5%/epoch; databases are migrated off this node immediately |
| **Stage 4 — Data Loss** | A database experiences data loss attributable to this node (WAL gap, safekeeper failure without recovery) | 20% immediate slash + 5%/epoch; node removed from registry; tenants notified |

**Stage 3 trigger: live database migration.** When a compute node enters Stage 3, any databases it hosts must be migrated to another compute node. The migration process:
1. The platform selects a peer compute node with sufficient capacity.
2. The primary Postgres process on the failing node is gracefully shut down.
3. The new compute node starts Postgres, pointing at the same Tier 1 blob storage bucket (no data movement required — pages are in blob storage, not the compute node).
4. The proxy layer is updated to route to the new compute node.
5. Target downtime during migration: under 30 seconds.

---

## Staking

Compute node operators stake under a **distinct on-chain node type, `Compute`** — not the platform's general `OLTP` node type, which is reserved for the martech track's internal transactional layer (see [Node Types and Rewards](../Platform/node-types-and-rewards.md#oltp-node)). This distinction was confirmed during Arch review of node onboarding (#90): compute nodes scale stake continuously with declared vCPU capacity (matching Storage's per-TB shape), rather than OLTP's tiered per-compute-size-step formula, and compute nodes are fee-funded rather than emission-funded (see Reward Pool, above), making them a materially different economic object from OLTP despite the superficial "both run Postgres" similarity.

- The minimum stake per vCPU offered scales similarly to storage nodes per GB (continuous, not stepped).
- Staking requirements and exact rates are governance parameters — see [Tokenomics MVP §9.1](../Blockchain/tokenomics-mvp.md#91-by-node-type) for illustrative figures.
- Unstaking follows the same cooldown period as storage nodes.

Compute node operators may also run storage nodes, or a martech OLTP node, on separate hardware. Each role uses its own stake deposit and its own reward stream — a `Compute` node's stake is entirely independent of any `OLTP` or `Storage` stake the same operator holds.

---

## Onboarding

Compute node onboarding follows the same general flow as storage node onboarding (see [Node Runner Onboarding and Tooling](../Network/node-runner-onboarding-and-tooling.md)):

1. Account creation and KYC (same requirements as storage operators).
2. Node software installation (a separate compute node daemon binary, not the storage node binary).
3. Benchmark suite: CPU (MIPS benchmark), RAM bandwidth, local disk IOPS, network latency to peer compute nodes in the target region.
4. Tier classification based on benchmark results.
5. Staking.
6. Registration on-chain and verifier-witnessed benchmarks.

**Additional compute-specific requirements:**
- The operator must configure which Postgres major version(s) the node supports.
- The operator must declare which extensions are installed and available on the node.
- HIPAA-workload eligibility requires BAA execution before the node is eligible to host HIPAA-designated databases.

---

## Capacity Limits Per Node

A compute node declares the maximum number of databases it can host simultaneously and the maximum aggregate vCPU and RAM it can offer across all hosted databases. The platform's orchestration layer respects these limits when placing new databases.

Recommended limits to prevent over-subscription:
- Total active vCPUs allocated to hosted databases ≤ 80% of physical core count.
- Total RAM allocated ≤ 85% of available RAM (leaving headroom for OS and pageserver cache overhead).

Nodes that are over-subscribed (allocated more than they can serve) are detected by elevated query latency and CPU saturation metrics and enter Stage 1 automatically.
