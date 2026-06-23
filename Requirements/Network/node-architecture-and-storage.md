# Node Architecture and Storage

## Overview

The system is composed of autonomous storage nodes. Each node stores blobs on its local disk and participates in the broader cluster. Capacity scales horizontally by adding more nodes to the network.

## Local Storage

- Each node stores blobs as files on its local filesystem. No external database or distributed filesystem is required for the blob data itself.
- The node operator configures the storage root path and the maximum capacity the node offers to the network.
- The node tracks used vs. available capacity and reports this to the network continuously.
- Blob metadata (checksums, size, region tags, replica locations, tenant ownership) is stored locally and synchronized to the coordination layer (blockchain or off-chain index with on-chain roots).

## Cluster Extensibility

- Adding a new node to the cluster adds its offered capacity to the total available storage of the network.
- Nodes participate in a peer-to-peer network for data replication, metadata gossip, and performance measurement.
- No single node is a mandatory coordinator; the design must tolerate any individual node going offline without making the cluster inoperable.
- Node discovery uses a well-known bootstrap set and/or on-chain node registry. NAT traversal support is required.

## Node Capacity Management

- Node operators declare the amount of storage they are offering at registration time and may adjust it at any time.
- **Scaling up**: The node announces additional available capacity; the network begins routing new writes to it as it is verified and trusted.
- **Scaling down**: The operator may reduce their offered capacity. Before reducing, the node must ensure all blobs it holds that would exceed the new capacity are replicated elsewhere. The network must re-place those blobs before the reduction takes effect.
- Operators who reduce their storage offering are subject to a **disincentive period** (see [Node Runner Incentives and Penalties](./node-runner-incentives-and-penalties.md)): their token earning rate is reduced for a defined period following a capacity decrease, to discourage churn and to compensate for the disruption caused to the network.

## Node Identity

- Each node has a persistent cryptographic identity (public/private keypair) generated at first launch.
- The node's public key serves as its unique identifier on the network and on-chain.
- Node identity is tied to its region claim and verification record (see [Region Identification and Verification](./region-identification-and-verification.md)).

## Operational Metrics Exported Per Node

Metrics are reported to the coordination layer continuously at a sub-epoch cadence (every 5 minutes by default). They form the basis of token reward calculations, performance tier assignment, and workload routing decisions. All metrics are self-reported by the node and subject to independent verification by verifier nodes — self-reported values that cannot be corroborated are weighted lower.

### Network Metrics

| Metric | Description |
|---|---|
| **Inbound bandwidth** | Measured upload capacity available to the node (Mbps), sampled under load. |
| **Outbound bandwidth** | Measured download capacity available to clients (Mbps). This is the primary bottleneck for serving reads. |
| **Average transfer speed** | Mean throughput of completed transfers during the sampling window (MB/s), distinct from raw bandwidth — reflects real-world utilization. |
| **Latency to peers** | Round-trip latency (ms) to a sample of peer nodes, used for region verification and performance scoring. |
| **Latency to clients** | Round-trip latency (ms) to recent clients (anonymized), sampled from completed requests. |
| **Jitter** | Variance in latency (ms) over the sampling window. High jitter degrades streaming and database-style workloads even when average latency looks acceptable. |
| **Packet loss rate** | Percentage of packets lost during the sampling window. Distinct from jitter; even low packet loss causes TCP retransmissions that significantly degrade throughput at scale. |
| **Time to first byte (TTFB)** | Median time (ms) from when a read request is received to when the first byte of the response is sent. Critical for latency-sensitive and database workloads. |
| **Connection concurrency** | Maximum number of simultaneous connections sustained without measurable latency degradation, measured during load testing at node registration and periodically thereafter. |

### Disk and I/O Metrics

| Metric | Description |
|---|---|
| **Disk type** | Self-reported and verified where possible: HDD, SSD (SATA), SSD (NVMe), or network-attached storage (NAS). Used for tier classification. |
| **Sequential read speed** | MB/s for large sequential reads, measured by the node daemon. Relevant for large file streaming workloads. |
| **Sequential write speed** | MB/s for large sequential writes. Relevant for ingest throughput. |
| **Random read IOPS** | Random 4K read operations per second at queue depth 1 and queue depth 32. The primary metric for database-grade workloads. |
| **Random write IOPS** | Random 4K write operations per second. |
| **I/O queue depth** | Average I/O queue depth under live load. A consistently high queue depth indicates the disk subsystem is saturated even if throughput looks acceptable. |
| **Available storage** | Total bytes offered to the network (configured by operator). |
| **Used storage** | Bytes currently occupied by stored blobs. |
| **Storage utilization %** | Used / available. SSDs in particular degrade in IOPS as utilization approaches 100%; nodes above 85% utilization are deprioritized for new writes. |
| **S.M.A.R.T. health indicators** | Reallocated sectors, pending sectors, uncorrectable errors, wear level (SSDs), and temperature, reported continuously. Used for early failure detection (see [Node Runner Incentives and Penalties](./node-runner-incentives-and-penalties.md)). |

### Compute Metrics

| Metric | Description |
|---|---|
| **CPU utilization** | Average CPU utilization (%) during the sampling window. High CPU under low I/O load may indicate a software bottleneck or resource contention from co-hosted workloads. |
| **Available memory for caching** | RAM available to the node daemon for blob caching (bytes). More cache memory means better hot-data TTFB for frequently accessed blobs. |
| **Active cache hit rate** | Percentage of read requests served from the in-memory cache without touching disk. High cache hit rate correlates with low TTFB for hot data. |

### Availability Metrics

| Metric | Description |
|---|---|
| **Uptime %** | Percentage of the current epoch the node has been reachable, measured by peer heartbeat checks. |
| **Heartbeat compliance %** | Percentage of expected sub-epoch heartbeats successfully submitted. Used for auto-payout eligibility (see [Node Runner Incentives and Penalties](./node-runner-incentives-and-penalties.md)). |
| **Blobs stored** | Count of distinct blobs currently held by this node. |
| **Total data served** | Cumulative bytes transferred to clients since the node joined the network. |
| **Request error rate** | Percentage of requests that returned an error (5xx) during the sampling window, excluding client errors (4xx). |

### Metric Verification

Self-reported metrics are verified by the network using the following methods:

- **Latency and jitter**: Independently measured by verifier nodes via active probing.
- **Bandwidth**: Verified via timed transfer tests initiated by verifiers.
- **IOPS and disk speed**: Verified at node registration via a standardized benchmark run witnessed by verifiers; re-benchmarked periodically.
- **Uptime and heartbeat**: Verified by cross-referencing heartbeat logs across multiple peers.
- **S.M.A.R.T. data**: Self-reported only; used for alerting, not for reward scoring.
- **Disk type**: Self-reported but cross-validated against measured IOPS — a node claiming NVMe speeds that only benchmarks at HDD speeds is classified at the measured tier, not the claimed one.
