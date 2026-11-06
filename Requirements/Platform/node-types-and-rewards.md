# Node Services and Dynamic Rewards

> **Vocabulary (#159).** A **Node** is one operator-run machine identity: one node key, one on-chain address. A Node runs **0, 1 or many Node Services**. Each Node Service is registered, staked, scored, penalized and paid on its own, so the more services a node runs to earn, the more it stakes. "Node type" and "role" are retired terms: older text that says "Storage node" means "a node running the Storage service". The contracts keep the Solidity identifier `NodeType` for ABI compatibility; it means *Node Service type*.

## Node Services at a glance

| Node Service | Earns SYM | Funding | Status |
|---|---|---|---|
| **Storage** | Yes | 92% capacity emission, Storage pool | Live |
| **Blob Gateway** | Yes | 92% capacity emission, Blob Gateway pool | Strawman ([#160](https://github.com/jamalkhan/Symposia/issues/160)) |
| **Verifier** | Yes | 8% verifier emission | Live ([#81](https://github.com/jamalkhan/Symposia/issues/81)) |
| **Blockchain Gateway** | Not yet defined | — | Foundation-run |
| **Telemetry Committee** | Not yet defined | — | Governance-appointed |
| **Email IP Address** | **Never** | 0%, hardcoded | Defined |
| **Compute** | Yes | Fee-funded compute pool | Pinned ([#162](https://github.com/jamalkhan/Symposia/issues/162)) |
| **OLTP**, **Analytics**, **Consensus**, **Serverless** | Yes | 92% capacity emission, own pools | Pinned ([#162](https://github.com/jamalkhan/Symposia/issues/162)) |

Per-service rules (on-chain, `StakingNodeRegistry` / `RewardDistributor`):

- **Stake:** `minStakeFor(service, capacity)` is required for *each* service. Unstake, top-up and the 21-day cooldown are per service.
- **Penalties:** a service-specific fault (the Storage or Compute penalty stages) slashes and deregisters only that service. Node-level violations (overcommitment, region-verification fraud, foundation commitment breaches) slash every service pro rata to stake and ban the whole node.
- **Rewards:** each service is scored against peers running the same service and settled separately. Payouts always go to the node address. The node's first-registered (*primary*) service settles under the node address; every other service under a derived *reward account* (`accountOf(node, service)`).
- **Region verification** is a fact about the node, so it applies to every service it runs.

The NodeHost **Node Services** tab shows each service's public earnings breakdown: pool, factor weights, minimum stake and penalties.

## Overview

The Symposia network is composed of nodes operated by independent node operators. Each node runs one or more Node Services, chosen at configuration time. Each Node Service has distinct resource requirements matched to its workload. Operators who guarantee and deliver those resources earn mining rewards; operators who miss resource availability commitments earn less.

The network's reward system is dynamic: mining yields for each Node Service adjust automatically based on the current network composition and actual workload demand. This creates a self-balancing incentive mechanism — operators naturally migrate toward underserved Node Services as rewards rise, and away from oversaturated services as rewards fall.

---

## Node Deployment Model

Nodes are deployed as **Docker containers**. A single physical machine can run multiple node containers, each running different Node Services or several instances of the same service. On-chain, a node's services are staked and scored separately. When an operator allocates resources to a container, they are making a **binding commitment** to the network:

- The declared RAM is reserved and guaranteed available to that container.
- The declared IOPS capacity is reserved and guaranteed not to be starved by co-located workloads.
- The declared storage capacity is provisioned and not double-allocated.
- The declared uptime SLA represents a real availability commitment.

**Resource misses have real consequences.** If a node fails to deliver on its committed resources when a workload request arrives — RAM pressure causes a swap, IOPS contention causes a timeout, the node is offline — the operator's mining yield for that epoch is reduced in proportion to the miss rate. Repeated misses can trigger escalating penalties. Operators who consistently deliver on their commitments earn the full base reward plus reliability bonuses.

---

## Blockchain Guarantees

The Symposia blockchain tracks and verifies four classes of resource provision. Each maps to one or more Node Services:

| Guarantee | What Is Tracked | Measured By |
|---|---|---|
| **Compute time and performance** | CPU cycles delivered at committed clock speed; query latency SLAs | Per-request latency attestations, challenge-response proofs |
| **Space (storage) time** | Data stored for the committed duration at the committed capacity | Proof-of-storage challenges at random intervals |
| **Uptime** | Node reachability and responsiveness over time | Heartbeat protocol; missed heartbeats reduce uptime score |
| **Memory time and performance** | RAM availability and bandwidth at declared capacity | Memory-intensive workload benchmarks run by the challenge protocol |

These guarantees are the basis for both reward calculation and workload scheduling — the platform routes workloads to nodes whose guarantee profile matches the workload's requirements.

---

## Node Services

### OLTP

Runs the Postgres-compatible transactional database layer (contact records, segmentation queries, event writes) for the martech platform's own internal use.

> **Not the same as `Compute` nodes.** Symposia's tenant-facing Postgres-as-a-service offering ([Compute Nodes](../Database/compute-nodes.md)) is a separate, later-added participant class with its own `NodeType` (`Compute`, not `OLTP`) and its own fee-funded reward pool, rather than the emission-funded pool described below. The two were conflated in earlier tokenomics drafts; see the correction in [Tokenomics MVP §6.1](../Blockchain/tokenomics-mvp.md#61-base-pool-weights-before-demand-adjustment).

| Resource | Requirement | Rationale |
|---|---|---|
| RAM | Moderate (8–32 GB) | Shared buffers, working memory for complex queries |
| IOPS | **High** | Random read/write patterns; sequential scan for large tables |
| CPU | Moderate | Query execution; parallel scans |
| Storage | Moderate | WAL + data files; blob handles bulk archival |
| Uptime | High (≥99.5%) | Transactional workloads cannot tolerate frequent node failure |

Primary blockchain guarantees: **Compute time/performance**, **Uptime**.

---

### Storage

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

### Analytics

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

### Consensus

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

### Serverless

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

### Email IP Address

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

**Inbound vs outbound roles:** Logical capabilities on the same Email IP edge (`outbound`, `inbound`, or `combined`). Default is combined on one agent/server. Large tenants may split fleets (and optionally IPs) for scale and isolation without inventing a separate mining Node Service. See [Inbound vs outbound roles](../Messaging/outbound-email-delivery.md#inbound-vs-outbound-roles-logical-split-flexible-deployment).

#### Rewards: none

| Rule | Detail |
|---|---|
| **No mining rewards** | The Email IP Address service **does not earn** epoch token emission, dynamic reward multipliers, or reliability bonuses. Clusters and extra workers also earn nothing. |
| **Not in reward supply math** | Network composition metrics for OLTP/Storage/Analytics/Consensus **exclude** the Email IP Address service when computing demand/supply multipliers. |
| **Function only** | The **only** function is outbound + inbound message relay (and edge buffering) for the owning tenant’s bound domains. |
| **No cross-tenant work** | Node must not accept or send mail for other tenants; not an open relay. |

Uptime and health are still monitored for **routing and deliverability** (alerts, failover, send suspension). Failure degrades that marketer’s email path only; it does not slash mining stake (there is no mining stake for this service). Optional abuse-related holds are account/compliance actions, not protocol slashing.

Primary blockchain guarantees: **none for rewards**. Optional on-chain registration of node public key + tenant binding for auditability of which IP path a domain used.

---

### Blob Gateway

The S3-compatible front door to Storage ([gateway architecture](../BlobStorage/gateway-architecture.md)): it authenticates tenants, writes objects to Storage services up to the write quorum, repairs missing copies and serves reads. It holds no data at rest.

**Earnings (strawman, #160):** a type pool inside the 92% capacity emission (MVP deploy: Storage 90% / Blob Gateway 10% of the capacity slice; long-term base weight 4%). It is scored on uptime 30%, request success rate 30%, p95 latency 20% (lower is better) and bytes served 20%. Minimum stake is 10,000 SYM flat. There are no data-loss stages; failed requests lower the score instead.

---

### Verifier

Checks other nodes' claims (latency, retrieval, region) and attests to them on-chain ([verifier nodes](../Network/verifier-nodes.md)).

**Earnings (#81):** the 8% verifier emission slice, split by score. It is scored on uptime 40%, coverage (assigned checks completed) 30% and accuracy (attestations upheld) 30%. Minimum stake is 125,000 SYM (5× the Storage base). Verification fraud is a node-level violation.

---

### Blockchain Gateway

Relays registrations, metric reports, telemetry and epoch seals to the chain and pays their gas. Foundation-run today; NodeHost starts one in Local dev and Remote RPC modes. **Earnings: not yet defined.**

---

### Telemetry Committee

Checkpoints heartbeat telemetry hourly and aggregates per-epoch records on-chain (#158). Members are appointed by governance today. **Earnings: not yet defined.**

---

## Dynamic Reward System

### Principle

Mining rewards for each Node Service are dynamically adjusted based on **current network composition relative to actual workload demand**. Node Services that are undersupplied relative to demand earn higher rewards; oversupplied services earn less. This creates a continuous self-balancing mechanism without requiring manual intervention.

### How Rewards Are Calculated

Each epoch (e.g., one hour), the network measures:

- **Node count per type** — how many nodes of each type are currently active and meeting their resource commitments.
- **Workload demand per type** — how much of each resource class is actually being consumed by platform workloads (queries executed, bytes stored, blocks validated).

The reward multiplier for a Node Service in a given epoch is approximately:

```
reward_multiplier(type) = base_rate × (demand_utilization(type) / network_supply(type))
```

Where `demand_utilization(type)` is the fraction of committed capacity actually being consumed, and `network_supply(type)` is the fraction of total nodes running that type.

In plain terms: **scarce and busy = high reward; abundant and idle = low reward.**

### Example

Today's network: 30% OLTP, 40% Storage, 30% Analytics. Workload is balanced across types.

| Node Service | Network Share | Utilization | Reward Units / Epoch |
|---|---|---|---|
| OLTP | 30% | High | 3 |
| Storage | 40% | High | 4 |
| Analytics | 30% | Moderate | 3 |

One year from now: the platform has grown, but operators have over-indexed on OLTP and Storage. Analytics nodes are scarce.

| Node Service | Network Share | Utilization | Reward Units / Epoch |
|---|---|---|---|
| OLTP | 50% | Moderate | 2 |
| Storage | 30% | High | 5 |
| Analytics | 20% | Very High | 8 |

An operator watching these signals would be incentivized to spin up analytics nodes — which is exactly the outcome the network needs. As more analytics nodes come online and utilization drops, the analytics reward naturally decreases back toward equilibrium.

### Bonus Incentives

When organic reward adjustment is not moving the network fast enough — for example, a new Node Service is launched and the network needs rapid capacity — the platform can issue **time-limited bonus incentives**:

- A bonus multiplier applied on top of the base reward for a targeted Node Service.
- Duration is fixed (e.g., 30 days) and announced in advance.
- Bonuses are funded from the platform's reserve token pool, not from other Node Services' rewards.

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

## Future Node Services

As the platform adds capabilities, additional Node Services may be introduced. Candidates:

| Future Node Service | Primary Workload | Resource Profile |
|---|---|---|
| **Edge / CDN** | Asset delivery, landing pages, email images | High bandwidth, low latency, distributed geography |
| **ML / Inference** | AI scoring, purchase propensity, send-time optimization | High GPU or high CPU, moderate RAM |
| **Messaging Relay** | NATS JetStream hosting, pub/sub routing | High network throughput, moderate CPU/RAM |

**Serverless Function** is **not** future — it is a first-class rewarded Node Service above. Product surface (deploy API, runtimes, limits) may ship in phases; the service and mining parameters are defined for MVP economics.

Each new Node Service goes through a governance process before launch: resource requirements are published, reward parameters are set, and a bootstrapping bonus period is announced to seed initial supply. **Full extensibility model** (apps vs Node Services, weight renormalization, vendor path): [Extensible Node Services & Application Platform](./extensible-node-types-and-app-platform.md).

---

## Open Questions

1. ~~**Epoch length**~~ **Resolved:** **24 hours** UTC. See [Tokenomics MVP](../Blockchain/tokenomics-mvp.md#4-epochs).

2. ~~**Reward token mechanics**~~ **Resolved:** Paid in native token (**SYM** provisional). **Fixed declining emission** from Network Rewards bucket over ~8 years; after cap, **fee-funded floor** (no uncapped mint). Dynamic **type multipliers** rebalance mix; emission total is not “print to match revenue.” See [Tokenomics MVP](../Blockchain/tokenomics-mvp.md).

3. ~~**Minimum stake per Node Service**~~ **Resolved:** See [Tokenomics MVP §9](../Blockchain/tokenomics-mvp.md#9-staking-minimums-mvp) (Storage / OLTP / Analytics / Consensus / Verifier tables).

4. ~~**Cross-node-type operators**~~ **Resolved:** Co-location **allowed**; protocol enforces **per-container resource guarantees** via reliability scoring, not mandatory dedicated hardware. Isolation recommended for heavy OLTP+Analytics. See [Tokenomics MVP §9.3](../Blockchain/tokenomics-mvp.md#93-cross-type-co-location-isolation).
