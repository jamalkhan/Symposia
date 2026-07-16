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

### Serverless Function Node

**Purpose:** Run short-lived, event-driven compute for the platform — webhooks, Journey action side-effects, data transforms, automation hooks, light API glue. This is the “Lambda-like” tier: **invoke → run → scale to zero**, not long-running Postgres or analytics processes.

| Resource | Requirement | Rationale |
|---|---|---|
| RAM | Moderate–high per worker (e.g. 1–8 GB per concurrent slot) | Isolate function memory; cold starts |
| IOPS | Low–moderate | Ephemeral scratch; optional cache |
| CPU | **Burst-capable**, multi-core preferred | Short CPU-heavy handlers |
| Storage | Minimal local (ephemeral) | Code packages in blob; no tenant durable store on the node |
| Network | High egress to tenant webhooks / APIs | Outbound HTTP is the common path |
| Uptime | Moderate–high (≥99%) for the **control plane** accepting invokes | Individual workers may churn |

**Workload model:**

- Platform (or tenant-triggered Journey/webhook) schedules an **invocation**.  
- Node pulls function artifact (from blob), runs in a **sandboxed** runtime (time limit, memory limit, no raw host access).  
- Result/logs returned; billable unit is **GB-second** (and/or invoke count) — exact metering in a future Serverless product spec; economics treat committed **concurrent slots** + delivered invoke time as demand signals.

**Not the same as:**

| Type | Difference |
|---|---|
| OLTP | Long-lived Postgres; not request-scoped sandboxes |
| Analytics | Heavy scans; minutes-long queries, not 100ms–60s functions |
| Email IP | SMTP relay only; no general compute; **no mining** |

Primary blockchain guarantees: **Compute time/performance**, **Uptime** (accept path).

---

### Email IP Address Node

**Operator:** Marketer / tenant (or their infrastructure provider), **not** a general mining node runner.

**Purpose:** Supply the marketer’s public email IP address(es) to the network and act as the **inbound and outbound SMTP proxy** for all mail for domains bound to that node. Full product requirements: [Outbound Email Delivery — Email IP Address Nodes](../Messaging/outbound-email-delivery.md#email-ip-address-nodes).

| Resource | Requirement | Rationale |
|---|---|---|
| RAM | Low (1–4 GB) | Relay only; no mailbox store |
| IOPS | Low | Queue spill to disk optional |
| CPU | Low–moderate | TLS, connection concurrency |
| Storage | Minimal | Spool for deferred outbound/inbound only |
| Network | **Public IP required**; stable egress; port 25 capability | ISP-facing SMTP |
| Uptime | High for production sending | Outages block or delay that tenant’s mail |

**Multiplicity:** A marketer may run **multiple** mail endpoints (multiple public IPs / domains / failover) and may **cluster** multiple agents/workers behind a single public mail IP for capacity and buffering. See [Outbound Email — Clustering](../Messaging/outbound-email-delivery.md#clustering-behind-a-mail-ip).

**Clustering:** Allowed. The address registered to the network must remain a **sendable and receivable** mail IP (SNAT/VIP egress and MX ingress agree with SPF/rDNS). Workers behind the VIP do not each need a distinct public IP. Reputation and warm-up attach to the **registered mail IP**, not to individual workers. Large tenants use cluster-local spool to buffer outbound blasts and inbound bounce/FBL load.

**Inbound vs outbound roles:** Logical capabilities on the same Email IP edge (`outbound`, `inbound`, or `combined`). Default is combined on one agent/server. Large tenants may split fleets (and optionally IPs) for scale and isolation without inventing a separate mining node type. See [Inbound vs outbound roles](../Messaging/outbound-email-delivery.md#inbound-vs-outbound-roles-logical-split-flexible-deployment).

#### Rewards: none

| Rule | Detail |
|---|---|
| **No mining rewards** | Email IP Address nodes **do not earn** epoch token emission, dynamic reward multipliers, or reliability bonuses. Clusters and extra workers also earn nothing. |
| **Not in reward supply math** | Network composition metrics for OLTP/Storage/Analytics/Consensus **exclude** Email IP Address nodes when computing demand/supply multipliers. |
| **Function only** | The **only** function is outbound + inbound message relay (and edge buffering) for the owning tenant’s bound domains. |
| **No cross-tenant work** | Node must not accept or send mail for other tenants; not an open relay. |

Uptime and health are still monitored for **routing and deliverability** (alerts, failover, send suspension). Failure degrades that marketer’s email path only; it does not slash mining stake (there is no mining stake for this node type). Optional abuse-related holds are account/compliance actions, not protocol slashing.

Primary blockchain guarantees: **none for rewards**. Optional on-chain registration of node public key + tenant binding for auditability of which IP path a domain used.

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

As the platform adds capabilities, additional node types may be introduced. Candidates:

| Future Node Type | Primary Workload | Resource Profile |
|---|---|---|
| **Edge / CDN** | Asset delivery, landing pages, email images | High bandwidth, low latency, distributed geography |
| **ML / Inference** | AI scoring, purchase propensity, send-time optimization | High GPU or high CPU, moderate RAM |
| **Messaging Relay** | NATS JetStream hosting, pub/sub routing | High network throughput, moderate CPU/RAM |

**Serverless Function** is **not** future — it is a first-class rewarded type above. Product surface (deploy API, runtimes, limits) may ship in phases; the node type and mining parameters are defined for MVP economics.

Each new node type goes through a governance process before launch: resource requirements are published, reward parameters are set, and a bootstrapping bonus period is announced to seed initial supply. **Full extensibility model** (apps vs node types, weight renormalization, vendor path): [Extensible Node Types & Application Platform](./extensible-node-types-and-app-platform.md).

---

## Open Questions

1. ~~**Epoch length**~~ **Resolved:** **24 hours** UTC. See [Tokenomics MVP](../Blockchain/tokenomics-mvp.md#4-epochs).

2. ~~**Reward token mechanics**~~ **Resolved:** Paid in native token (**SYM** provisional). **Fixed declining emission** from Network Rewards bucket over ~8 years; after cap, **fee-funded floor** (no uncapped mint). Dynamic **type multipliers** rebalance mix; emission total is not “print to match revenue.” See [Tokenomics MVP](../Blockchain/tokenomics-mvp.md).

3. ~~**Minimum stake per node type**~~ **Resolved:** See [Tokenomics MVP §9](../Blockchain/tokenomics-mvp.md#9-staking-minimums-mvp) (Storage / OLTP / Analytics / Consensus / Verifier tables).

4. ~~**Cross-node-type operators**~~ **Resolved:** Co-location **allowed**; protocol enforces **per-container resource guarantees** via reliability scoring, not mandatory dedicated hardware. Isolation recommended for heavy OLTP+Analytics. See [Tokenomics MVP §9.3](../Blockchain/tokenomics-mvp.md#93-cross-type-co-location-isolation).
