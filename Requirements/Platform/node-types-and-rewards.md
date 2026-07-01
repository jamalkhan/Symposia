# Node Types and Dynamic Rewards

## Overview

The Symposia network is composed of specialized compute nodes operated by independent node operators. Operators deploy nodes as Docker containers, selecting a node type at configuration time. Each node type has distinct resource requirements matched to its workload. Operators who guarantee and deliver those resources earn mining rewards; operators who miss resource availability commitments earn less.

The network's reward system is dynamic: mining yields for each node type adjust automatically based on the current network composition and actual workload demand. This creates a self-balancing incentive mechanism — operators naturally migrate toward underserved node types as rewards rise, and away from oversaturated types as rewards fall.

---

## Node Deployment Model

Nodes are deployed as **Docker containers**. A single physical machine can run multiple node containers, each configured as a different node type or multiple containers of the same type. When an operator allocates resources to a container, they are making a **binding commitment** to the network:

- The declared RAM is reserved and guaranteed available to that container.
- The declared IOPS capacity is reserved and guaranteed not to be starved by co-located workloads.
- The declared storage capacity is provisioned and not double-allocated.
- The declared uptime SLA represents a real availability commitment.

**Resource misses have real consequences.** If a node fails to deliver on its committed resources when a workload request arrives — RAM pressure causes a swap, IOPS contention causes a timeout, the node is offline — the operator's mining yield for that epoch is reduced in proportion to the miss rate. Repeated misses can trigger escalating penalties. Operators who consistently deliver on their commitments earn the full base reward plus reliability bonuses.

---

## Blockchain Guarantees

The Symposia blockchain tracks and verifies four classes of resource provision. Each maps to one or more node types:

| Guarantee | What Is Tracked | Measured By |
|---|---|---|
| **Compute time and performance** | CPU cycles delivered at committed clock speed; query latency SLAs | Per-request latency attestations, challenge-response proofs |
| **Space (storage) time** | Data stored for the committed duration at the committed capacity | Proof-of-storage challenges at random intervals |
| **Uptime** | Node reachability and responsiveness over time | Heartbeat protocol; missed heartbeats reduce uptime score |
| **Memory time and performance** | RAM availability and bandwidth at declared capacity | Memory-intensive workload benchmarks run by the challenge protocol |

These guarantees are the basis for both reward calculation and workload scheduling — the platform routes workloads to nodes whose guarantee profile matches the workload's requirements.

---

## Node Types

### OLTP Node

Runs the Postgres-compatible transactional database layer (contact records, segmentation queries, event writes).

| Resource | Requirement | Rationale |
|---|---|---|
| RAM | Moderate (8–32 GB) | Shared buffers, working memory for complex queries |
| IOPS | **High** | Random read/write patterns; sequential scan for large tables |
| CPU | Moderate | Query execution; parallel scans |
| Storage | Moderate | WAL + data files; blob handles bulk archival |
| Uptime | High (≥99.5%) | Transactional workloads cannot tolerate frequent node failure |

Primary blockchain guarantees: **Compute time/performance**, **Uptime**.

---

### Storage Node

Runs the S3-compatible blob storage layer (Parquet event archives, Merkle batch files, tenant assets, consent banner copy).

| Resource | Requirement | Rationale |
|---|---|---|
| RAM | Low (4–8 GB) | Minimal processing; primarily I/O passthrough |
| IOPS | Moderate | Sequential large-file reads/writes dominate |
| CPU | Low | Hashing, encryption, and checksum verification |
| Storage | **High** | Primary workload: durable, large-scale object storage |
| Uptime | High (≥99.5%) | Stored data must be available for Merkle proof lookups |

Primary blockchain guarantees: **Space (storage) time**, **Uptime**.

---

### Analytics Node

Runs the DuckDB analytical query layer (campaign reports, engagement analytics, revenue attribution).

| Resource | Requirement | Rationale |
|---|---|---|
| RAM | **High** (32–128 GB) | DuckDB columnar scans spike to multiple GB per concurrent query during historical scans |
| IOPS | Moderate | Reading Parquet from local cache; cache miss triggers blob fetch |
| CPU | High | Columnar aggregation and sort operations are CPU-bound |
| Storage | Moderate | Local Parquet cache (pre-fetched from blob); summary tables |
| Uptime | Moderate (≥99.0%) | Analytics queries can be retried; slightly lower SLA than transactional nodes |

Primary blockchain guarantees: **Memory time/performance**, **Compute time/performance**.

---

### Blockchain / Consensus Node

Runs the chain consensus protocol, validates blocks, and records Merkle commitments.

| Resource | Requirement | Rationale |
|---|---|---|
| RAM | Moderate (8–16 GB) | Block validation and mempool |
| IOPS | Low to moderate | Chain state reads; block writes are sequential |
| CPU | Moderate | Cryptographic verification; consensus protocol |
| Storage | Moderate | Chain state and block history |
| Uptime | **Critical** (≥99.9%) | Consensus participation requires consistent availability |
| Network | High bandwidth, low latency | Block propagation is network-critical |

Primary blockchain guarantees: **Uptime**, **Compute time/performance**.

---

## Dynamic Reward System

### Principle

Mining rewards for each node type are dynamically adjusted based on **current network composition relative to actual workload demand**. Node types that are undersupplied relative to demand earn higher rewards; oversupplied types earn less. This creates a continuous self-balancing mechanism without requiring manual intervention.

### How Rewards Are Calculated

Each epoch (e.g., one hour), the network measures:

- **Node count per type** — how many nodes of each type are currently active and meeting their resource commitments.
- **Workload demand per type** — how much of each resource class is actually being consumed by platform workloads (queries executed, bytes stored, blocks validated).

The reward multiplier for a node type in a given epoch is approximately:

```
reward_multiplier(type) = base_rate × (demand_utilization(type) / network_supply(type))
```

Where `demand_utilization(type)` is the fraction of committed capacity actually being consumed, and `network_supply(type)` is the fraction of total nodes running that type.

In plain terms: **scarce and busy = high reward; abundant and idle = low reward.**

### Example

Today's network: 30% OLTP, 40% Storage, 30% Analytics. Workload is balanced across types.

| Node Type | Network Share | Utilization | Reward Units / Epoch |
|---|---|---|---|
| OLTP | 30% | High | 3 |
| Storage | 40% | High | 4 |
| Analytics | 30% | Moderate | 3 |

One year from now: the platform has grown, but operators have over-indexed on OLTP and Storage. Analytics nodes are scarce.

| Node Type | Network Share | Utilization | Reward Units / Epoch |
|---|---|---|---|
| OLTP | 50% | Moderate | 2 |
| Storage | 30% | High | 5 |
| Analytics | 20% | Very High | 8 |

An operator watching these signals would be incentivized to spin up analytics nodes — which is exactly the outcome the network needs. As more analytics nodes come online and utilization drops, the analytics reward naturally decreases back toward equilibrium.

### Bonus Incentives

When organic reward adjustment is not moving the network fast enough — for example, a new node type is launched and the network needs rapid capacity — the platform can issue **time-limited bonus incentives**:

- A bonus multiplier applied on top of the base reward for a targeted node type.
- Duration is fixed (e.g., 30 days) and announced in advance.
- Bonuses are funded from the platform's reserve token pool, not from other node types' rewards.

Bonuses are a deliberate, governance-approved mechanism — not an automatic process. They represent the network saying: "We need more of this, and we're willing to pay extra to get it fast."

### Reliability Component

The base reward calculation assumes the node is meeting its resource commitments. Actual rewards are multiplied by a **reliability score**:

```
actual_reward = base_reward × reliability_score(operator, node, epoch)
```

Reliability score starts at 1.0 and is reduced by:
- Failed resource challenges (RAM availability, IOPS performance)
- Missed uptime heartbeats
- Query timeouts attributable to the node (not the client)
- Proof-of-storage failures

Operators with a sustained reliability score above a threshold (e.g., ≥0.98 over 30 days) earn a **reliability bonus** on top of the base reward — rewarding consistency, not just raw capacity.

---

## Future Node Types

As the platform adds capabilities, new node types will be introduced. Candidates:

| Future Node Type | Primary Workload | Resource Profile |
|---|---|---|
| **Edge / CDN** | Asset delivery, landing pages, email images | High bandwidth, low latency, distributed geography |
| **ML / Inference** | AI scoring, purchase propensity, send-time optimization | High GPU or high CPU, moderate RAM |
| **Messaging Relay** | NATS JetStream hosting, pub/sub routing | High network throughput, moderate CPU/RAM |

Each new node type goes through a governance process before launch: resource requirements are published, reward parameters are set, and a bootstrapping bonus period is announced to seed initial supply.

---

## Open Questions

1. **Epoch length**: What is the reward calculation interval — hourly, daily, or per-block? Shorter epochs respond faster to network changes but add overhead to reward calculation and distribution.

2. **Reward token mechanics**: Are rewards paid in the platform's native token? Is there a fixed token emission schedule, or is emission dynamic (tied to platform revenue / usage fees)? This intersects with the broader tokenomics design, which is out of scope here but needs to be specified before the blockchain layer is implemented.

3. **Minimum stake per node type**: Node operators may be required to stake tokens to participate, with higher stakes required for higher-SLA node types (e.g., consensus nodes). Stake is slashed for reliability failures. Specific stake amounts are not yet defined.

4. **Cross-node-type operators**: An operator running OLTP and Analytics containers on the same machine faces real resource contention. Should the platform enforce isolation requirements (e.g., analytics containers must be on dedicated hardware if above a certain capacity commitment)? Or is this the operator's problem to manage within their resource guarantees?
