# Node Runner Incentives and Penalties

## Overview

Node runners are compensated with the native token for providing reliable, high-performance storage to the network. Reward calculations are multi-factor and performance-weighted: nodes that serve data faster and more reliably earn more. Nodes that disrupt the network through failures, capacity reductions, or dishonesty face economic penalties.

## Reward Factors

Token rewards are calculated per **epoch** (a defined time period, e.g., 1 hour or 1 day) based on a weighted combination of the following factors:

| Factor | Description |
|---|---|
| **Uptime** | Percentage of the epoch the node was online and reachable. |
| **Latency** | Average time-to-first-byte for blob retrievals served during the epoch. Lower is better. |
| **I/O Throughput** | Average read and write throughput (MB/s) measured during the epoch. |
| **Network Bandwidth** | Available and utilized inbound/outbound bandwidth. |
| **Available Storage** | Amount of storage capacity offered to the network (unused capacity still provides value). |
| **Used Storage** | Proportion of offered capacity actively storing data for tenants. |
| **Retrieval Speed** | End-to-end blob delivery speed as measured by verifiers and clients. This is the primary performance metric and carries the highest reward weight. |

- The exact weighting of each factor is a governance parameter (see Blockchain & Tokenomics requirements).
- Faster retrieval yields disproportionately higher rewards: the incentive design explicitly favors nodes that invest in good hardware, network peering, and low-latency infrastructure.
- Metrics are aggregated from: self-reported node telemetry (subject to challenge), verifier-observed measurements, and client-reported retrieval outcomes.

## Epoch Definition

An **epoch** is the fundamental time unit for reward accounting. It has the following properties:

- **Duration**: Each epoch lasts **24 hours** (1 calendar day, UTC-anchored). This is a governance parameter and may be adjusted, but daily epochs balance settlement frequency against on-chain overhead.
- **Block alignment**: An epoch begins and ends at a specific block number. The block at which a new epoch starts is deterministic and computable by any participant from the genesis block and the epoch length.
- **Finality window**: After the last block of an epoch closes, there is a **finality window** (e.g., 10 minutes) during which late-arriving metric reports are accepted. After the window closes, the epoch is sealed and no further data is incorporated.
- **Epoch index**: Epochs are numbered sequentially from genesis (Epoch 0, Epoch 1, …). The epoch index is used as the key for all reward and slash records on-chain.

### Within an Epoch

- Nodes continuously self-report metrics (uptime heartbeats, I/O and bandwidth samples) at a sub-epoch cadence (e.g., every 5 minutes). These reports are gossiped peer-to-peer and checkpointed to the chain periodically, not on every heartbeat.
- Verifier nodes sample peer latency and retrieval speed throughout the epoch. Their observations accumulate as signed attestations.
- The epoch's metric record for each node is the **aggregated, outlier-rejected summary** of all samples received during that epoch.

## Epoch Reward Calculation

At epoch seal (after the finality window closes):

1. Each node's per-factor scores are computed from the epoch's aggregated metric record.
2. Each factor score is normalized to a 0–1 range relative to the best-performing node in that factor during this epoch.
3. The weighted sum of normalized factor scores produces the node's **epoch score**.
4. All eligible nodes' epoch scores are summed to produce the **total epoch score**.
5. Each node's **reward share** = `(node epoch score) / (total epoch score)`.
6. The **epoch emission** (the number of new tokens minted this epoch, per the emission schedule) is multiplied by each node's reward share to determine its payout.
7. Payouts are distributed automatically at epoch seal to any node that submitted more than 90% of its expected heartbeats in both the current epoch and the immediately preceding epoch. Nodes that do not meet this threshold have their payout held in a claimable reserve; held rewards do not expire but do not compound. Once a node returns to >90% heartbeat compliance for 2 consecutive epochs, held reserves are automatically swept to the node on the next payout.

**Eligibility requirements** to receive any reward in an epoch:
- The node must have passed region verification before the epoch started (mid-epoch verification does not grant retroactive rewards for that epoch).
- The node must hold valid staked collateral above the minimum threshold for the full duration of the epoch.
- The node must have submitted at least one valid metric report during the epoch.

### Emission Schedule

- The total token emission per epoch follows a pre-defined, on-chain emission curve. The curve is set at genesis and is not changeable without a governance vote.
- Emission decreases over time (deflationary pressure) on a schedule defined in the Tokenomics specification.
- A portion of each epoch's emission is allocated to the **ecosystem reserve** (for grants, development, and bootstrap incentives) rather than node rewards. The split ratio is a governance parameter.

## Penalties and Slashing

The penalty system is **progressive by design**. The goal is to protect tenant data and incentivize reliable operation, not to punish honest operators who experience hardware failures. The system distinguishes between degraded-but-recoverable situations and actual, confirmed data loss — and escalates accordingly.

All percentages below are illustrative starting points subject to governance.

---

### Stage 1 — Warning (Epoch 1 of detected trouble)

Triggered by: missed heartbeats, rising checksum error rate, degraded I/O throughput, or failed proof-of-possession challenges — but no confirmed data loss yet.

**Effect:**
- Reward multiplier reduced to **70%** for the affected epoch.
- No stake is touched.
- Operator is alerted immediately (see Operator Alerting below).
- The network begins quietly pre-replicating the node's blobs to additional nodes as a precaution, so that if the situation worsens, redundancy is already being restored without waiting.

**Example:** A node with 10 tokens staked and normal earnings of 0.5 tokens/epoch earns 0.35 tokens this epoch. Stake is untouched.

---

### Stage 2 — Degraded (Epoch 2–3 of sustained trouble)

Triggered by: Stage 1 conditions persisting for 2 or more consecutive epochs without recovery, or a significant spike in integrity failures within a single epoch.

**Effect:**
- Reward multiplier reduced to **40%** for the affected epochs.
- No stake slash yet.
- Operator receives escalated alerts (see Alerting). Alerts explicitly recommend checking disk health (S.M.A.R.T. data), running a filesystem check, and considering voluntary capacity reduction to protect the network.
- The network accelerates re-replication of the node's blobs to reach the global redundancy target without this node.

**Example:** Same node, now in Stage 2 for 2 epochs. Earns 0.20 tokens/epoch instead of 0.50. Over 2 epochs that is 0.60 tokens lost relative to healthy earnings. Stake still untouched at 10 tokens.

---

### Stage 3 — Low-Rate Slash (Epoch 4+ or first confirmed partial data loss)

Triggered by: Stage 2 conditions persisting beyond 3 epochs without recovery, OR confirmed loss of data where redundancy for affected blobs dropped below the minimum replication factor (i.e., the network was left with only one or zero copies).

**Effect:**
- Reward multiplier reduced to **0%** (no earnings while in this stage).
- Stake is slashed at **5% per epoch** while in this stage, up to a maximum of 25% total across all Stage 3 epochs.
- Operator receives critical alerts. The node is suspended from receiving new storage deals.
- Existing blobs are actively migrated off the node by the network.

**Example:** Node has been degraded for 4 epochs and some blobs lost redundancy. Stage 3 begins.
- Epoch 4: 5% slash → 0.5 tokens deducted → stake now 9.5 tokens.
- Epoch 5: 5% slash → ~0.475 tokens deducted → stake now ~9.025 tokens.
- If the operator brings the node back online by Epoch 5 with verified healthy storage, they exit Stage 3 and enter recovery (see below). Remaining stake is intact.

---

### Stage 4 — High-Rate Slash (Confirmed Significant Data Loss)

Triggered by: confirmed permanent, unrecoverable loss of blobs where the network cannot restore redundancy from other replicas — meaning tenant data is actually gone.

**Effect:**
- Immediate additional stake slash of **20%** upon confirmation of unrecoverable loss.
- An additional **5% per epoch** continues while the node remains offline and affected.
- The node is removed from the active node registry and must re-register and re-verify to rejoin.
- The lost blobs are flagged to affected tenants.

**Example:** The node with 10 tokens staked suffers a total drive failure. ~800 GB of blobs were held solely on this node (the other replica was also offline — a worst-case scenario). After confirmation:
- Immediate 20% slash: −2.0 tokens → stake is now 8.0 tokens.
- If the node stays offline for 3 more epochs: −0.4 tokens/epoch → stake reaches ~6.8 tokens.
- The operator must top up their stake above the minimum before re-registering.

> **Note on 1 TB lost over 36 hours with 10 tokens staked (illustrative):** If the data loss is gradual (drive degrading over 36 hours = ~1.5 epochs), the system would progress through Stage 1, then Stage 2 before Stage 3 is reached. If the network's redundancy system had already pre-replicated most blobs during Stages 1–2, the final confirmed-loss count may be far below 1 TB, and the total slash might be 0.5–1.5 tokens rather than a catastrophic loss. Fast hardware-failure detection (see Alerting) is the key lever that keeps this number low.

---

### Recovery

A node exits the penalty stages when:
- It has been continuously online and passing all integrity checks for **3 consecutive epochs** after the triggering condition is resolved.
- Its stored blobs pass a full proof-of-possession audit.

On recovery:
- The reward multiplier is restored to 100%.
- Slashed tokens are **not returned** (they are burned or redistributed per the emission schedule), but no further slashing occurs.
- The node is re-eligible for new storage deals after 1 additional epoch of clean operation.

---

### Non-Hardware Violations

Some violations are treated differently from hardware failures because they reflect deliberate bad behavior:

| Violation | Penalty |
|---|---|
| **Overcommitment** (advertising more capacity than exists) | Immediate 15% stake slash, Stage 3 entry, no Stage 1/2 warning |
| **Region verification fraud** (deliberately faking location) | Immediate 30% stake slash, node banned from re-registration for 30 days |
| **Repeated verification failure** (3+ failed re-verification challenges) | 10% slash per failure after the second, no grace period |

These bypass the progressive stages because they represent intentional deception rather than hardware failure.

---

### Operator Alerting

The following alerting mechanisms are available to node operators. The goal is to surface problems as early as possible — ideally before the network even detects them.

**Node-local alerts (earliest warning):**
- The node daemon continuously monitors local disk health via **S.M.A.R.T. data** (reallocated sectors, pending sectors, uncorrectable errors, temperature). A threshold breach triggers an immediate local alert before any network penalty is issued.
- Filesystem error rates and I/O latency spikes are monitored and logged.

**Operator-configured delivery channels:**
- **Webhook**: Operators configure a URL at node setup. The node POSTs a structured JSON payload for every alert event (stage change, S.M.A.R.T. warning, slash event).
- **Email**: An operator email address is registered at node setup. Alerts are sent directly.
- **On-chain events**: Every penalty stage transition and slash event is emitted as an on-chain event. Any external monitoring tool (dashboards, PagerDuty, etc.) can subscribe to the node's on-chain identity and react to these events.

**Alert severity levels:**
- `INFO` — Stage 1 entry, minor S.M.A.R.T. warnings, reward reduction.
- `WARNING` — Stage 2 entry, escalating integrity errors. Message explicitly recommends checking hardware.
- `CRITICAL` — Stage 3 entry, first stake slash, node suspended from new deals.
- `EMERGENCY` — Stage 4 entry, confirmed data loss, large slash.

All alert payloads include: the node ID, the current penalty stage, the specific triggering metric, the action taken (reward reduction or slash amount), and a human-readable recommendation ("Check S.M.A.R.T. health on drive /dev/sda", "Consider reducing offered capacity to prevent further data loss").

## Capacity Reduction Disincentive

- When a node operator reduces their offered storage capacity, the network must re-place the displaced blobs onto other nodes. This is disruptive and consumes network resources.
- To discourage unnecessary churn, any capacity reduction triggers a **disincentive period**:
  - For a defined number of epochs following the reduction, the node's reward multiplier is reduced (e.g., to 50-75% of its score-based reward).
  - The length and severity of the disincentive scales with the size of the capacity reduction.
  - The disincentive period resets if the operator reduces capacity again before recovery.
  - The disincentive does not apply to capacity reductions caused by hardware failure (distinguishable from voluntary reductions by on-chain evidence).
- These parameters are governance-controlled.

## Staking Requirements

- Node operators must maintain a minimum stake proportional to their offered storage capacity at all times.
- If a slash reduces the stake below the minimum, the node is automatically suspended from new deals and rewards until the stake is topped up.
- Operators may voluntarily unstake after a cooldown period (to allow in-flight deals to be re-placed); immediate unstaking is not permitted.
