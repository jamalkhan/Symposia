# Redundancy and Data Integrity

## Overview

Every blob stored in the network must be durable and available even when individual nodes fail. The system enforces copy counts and placement rules based on the blob's region assignment, monitors replica health continuously, automatically repairs lost replicas, and proactively adds redundancy whenever a node's health begins to degrade.

---

## Copy Count and Placement Rules by Region Assignment

The minimum number of copies and their required placement is determined by the number of regions assigned to a blob (see Region Assignment requirements). These are hard invariants — the system must never allow a blob to fall below its target copy count without immediately triggering repair.

### Zero Regions (Best Effort / Global)

- **4 copies**, distributed across **at least 3 distinct regions**.
- No region is required, but placement must maximize geographic diversity.
- The 4 nodes selected must not share a fault domain (see Fault Domain rules below).

### One Region

- **5 copies total**:
  - 3 copies within the target region.
  - 1 copy in a second (overflow) region.
  - 1 copy in a third (overflow) region.
- The overflow copies provide resilience against a full region outage.
- The 3 copies within the target region must be placed on nodes that do not share a fault domain.

### Two Regions

- **6 copies total**:
  - 3 copies in the primary target region.
  - 2 copies in the secondary target region.
  - 1 copy in a third (overflow) region.
- The overflow copy ensures that even if both targeted regions experience simultaneous issues, at least one copy exists elsewhere.

### Three Regions

- **7 copies total**:
  - 2 copies in each of the 3 targeted regions (6 copies).
  - 1 copy in a fourth (overflow) region.

### Four or More Regions

- **2 copies in each targeted region** (minimum; no overflow requirement since spread is already high).
- Total copies = 2 × (number of target regions).

### Summary Table

| Region Assignment | Copies in Target Region(s) | Overflow Copies | Total |
|---|---|---|---|
| Zero (global) | — | 4 across ≥3 regions | 4 |
| One region | 3 | 1+1 in two other regions | 5 |
| Two regions | 3+2 | 1 in a third region | 6 |
| Three regions | 2+2+2 | 1 in a fourth region | 7 |
| Four+ regions | 2 per region | None | 2N |

---

## Fault Domain Rules

A **fault domain** is a set of nodes that share a common failure risk — a power circuit, a network uplink, a rack, a data center, or an ISP. Two replicas of the same blob must never share a fault domain, because a single outage would eliminate multiple copies simultaneously.

The following fault domains are enforced, in order of strictness:

1. **Same public IP address** — hard rule, never co-place two copies of the same blob on nodes sharing a public IP. This covers nodes behind the same NAT, same residential router, or same VM host presenting a shared egress IP.
2. **Same /24 subnet** — strong preference not to co-place; same subnet implies shared last-mile infrastructure. Enforced as a soft rule (prefer to avoid, but permitted if no alternative exists and the deficit is flagged).
3. **Same ASN (Autonomous System Number)** — prefer to avoid co-placing replicas within the same ISP or hosting provider, as an AS-level outage or BGP issue can affect all nodes within it simultaneously. Treated as a soft rule.
4. **Same verified physical facility** — if two nodes are verified to be in the same data center or co-location facility, prefer not to co-place. Treated as a soft rule.

Soft-rule violations are permitted only when the network does not have enough eligible nodes to fully comply. In that case, the placement engine logs the violation and records it in blob metadata as a known placement constraint gap.

---

## Penalty-Triggered Pre-Replication

When a node enters any penalty stage (see Node Runner Incentives & Penalties requirements), the system does not wait for data loss to occur before acting. The response is immediate and proportional:

| Node Penalty Stage | Replication Response |
|---|---|
| **Stage 1** (Warning) | Begin quietly creating 1 additional copy of every blob on this node, targeting nodes in different fault domains. The blob's effective copy count increases to minimum + 1 as a buffer. |
| **Stage 2** (Degraded) | Accelerate replication. Target minimum + 2 additional copies. The node is deprioritized for serving reads; healthy replicas take over. |
| **Stage 3** (Slashing / Suspended) | Treat the node as effectively offline for placement purposes. Begin migrating all of its blobs to other nodes. The node is removed from the read-serving pool immediately. |
| **Stage 4** (Confirmed loss) | All blobs formerly attributed to this node are audited. Any blob still below minimum copy count is placed at the highest repair priority. |

Pre-replication is throttled to avoid saturating the node or the network — a bandwidth budget for background replication is enforced per-node (see Bandwidth Budgeting below). The pre-replication tasks are visible in blob metadata as "pending replica" records, and their completion is tracked on-chain.

---

## Offline and Degradation Triggers

The following events trigger automatic repair or replication, in priority order:

### P0 — Immediate (within 5 minutes)

- A blob has **1 or 0 healthy copies** remaining.
- A blob is below its minimum copy count **and** all remaining copies are on nodes in penalty Stage 2 or higher.
- A read request returns a checksum mismatch from a replica (**read repair**): the bad replica is flagged and a fresh copy is created immediately from a healthy replica.

### P1 — High Priority (within 30 minutes)

- A blob drops below its minimum copy count for the first time (e.g., a node goes offline).
- A node enters penalty Stage 2 and blobs on that node are not yet pre-replicated from Stage 1.
- A node's region verification is revoked, and blobs on that node have a region constraint requiring that region.

### P2 — Standard Priority (within 2 hours)

- A blob is at its minimum copy count but one or more copies are on a Stage 1 penalty node.
- A blob has a fault domain violation (soft rule) due to recent cluster changes, and a better placement now exists.
- A node has been offline for longer than the **grace period** (see below) and repair has not yet been triggered.

### P3 — Background (within 24 hours)

- A blob is at its minimum copy count with no degraded copies, but the network has grown and additional capacity is available in new regions — increase redundancy opportunistically.
- A blob's overflow copy is in the same region as another copy due to limited region availability at write time; a new region now has nodes, so improve the placement.

### Grace Period for Transient Outages

Before triggering P1 or P2 repair on a node going offline, the system waits for a **grace period of 15 minutes**. This prevents thrashing when a node reboots, experiences a brief network hiccup, or restarts after an update. If the node comes back online within the grace period with all blobs intact, no repair is triggered.

The grace period does **not** apply in P0 situations (single copy remaining), where repair is immediate regardless.

### Replication Bandwidth Budgeting

Background replication (penalty-triggered or repair) is throttled per node to a configurable fraction of available bandwidth (default: 25% of measured upload capacity). This ensures that repair work does not degrade the node's ability to serve live client reads. The budget is a governance parameter and may be increased for nodes actively in Stage 3/4 recovery.

---

## Read Behavior When Replicas Are Degraded

- Reads are always routed to the **healthiest available replica**, ranked by the node's current performance score and penalty stage. A node in Stage 2 or higher is deprioritized but not excluded from reads until Stage 3.
- If all replicas of a blob are temporarily offline, the read returns a `503 Service Unavailable` with a `Retry-After` header. The error distinguishes between "temporarily unavailable" and "permanently lost."
- If a partial read (range request) encounters a mid-stream checksum failure, the connection is terminated and the client is instructed to retry; the system simultaneously flags that replica for read repair.

---

## Erasure Coding

- For storage efficiency beyond simple mirroring, the system supports **erasure coding** (e.g., Reed-Solomon) as an alternative or complement to full replication.
- Erasure coding reduces raw storage overhead (e.g., a 4+2 scheme requires 1.5× storage vs. 2× for full replication) while maintaining the same or better durability.
- The encoding scheme is configurable per bucket or globally, with simple replication as the default for initial deployments.
- Copy count rules above refer to logical copies; an erasure-coded blob's shard distribution must still satisfy the fault domain and region placement rules.

---

## Cryptographic Data Integrity

- Every blob is hashed on ingest (SHA-256 or a content-addressed identifier such as a CID).
- The hash is stored in blob metadata and verified on every read. A read returning data that does not match the stored hash fails immediately and triggers read repair.
- Nodes periodically re-verify stored blob hashes (**proof of possession**) and report results to the coordination layer. The verification schedule is randomized to prevent coordinated attestation fraud.
- A node that consistently fails integrity checks progresses through the penalty stages (see Node Runner Incentives & Penalties requirements).

## Proofs of Storage and Retrieval

- The system supports on-chain or verifiable **proofs of storage**: cryptographic evidence (e.g., a Merkle inclusion proof) that a node currently holds a specific blob, without transferring the full blob.
- **Challenge-response**: The coordination layer may issue a random challenge to any node at any time, requiring it to produce a proof for a randomly sampled byte range of a stored blob. Failure to respond correctly within a timeout counts as an integrity failure.
- Proofs of retrieval (evidence that a node can serve a blob to a requester at a given speed) are the primary metric for performance-weighted rewards.

## Increasing Redundancy Over Time

- As the network grows and more storage capacity becomes available, the system automatically increases redundancy for existing blobs beyond the minimum, via the P3 background process above.
- Maximum replication factors and the growth schedule are governance parameters.
- Blobs stored under the zero-region policy benefit most from this, as new region coverage can be added without any tenant action.
