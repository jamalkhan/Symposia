# Verifier Nodes

## Overview

Verifier nodes are the trust anchors of the network. They independently measure the performance and geographic location of other nodes, produce signed attestations that are recorded on-chain, and ensure that self-reported metrics cannot be fabricated. Without verifiers, every node's claims about its tier, region, and uptime are unverifiable self-reports. With verifiers, those claims must be consistent with what independent third parties observe.

Verifier nodes are a distinct operational role within the network. Not every storage node is a verifier, and not every verifier must run storage. The roles may be combined, but their responsibilities and reward structures are separate.

---

## What Verifiers Do

### Region Verification

When a new node submits a region claim (see [Region Identification and Verification](./region-identification-and-verification.md)), a set of verifiers is selected to measure the claimant's network characteristics:

- Round-trip latency from multiple geographic vantage points.
- Jitter (variance in latency over multiple measurements).
- Hop count via traceroute.
- Packet loss rate.
- Bandwidth (timed download test).

Verifiers aggregate their independent measurements, apply statistical outlier rejection, and compare the result against expected characteristics for the claimed region. If consistent, the verifier signs an attestation confirming the region claim. The attestation and all underlying measurements are posted on-chain.

### Periodic Re-Verification

Verifiers continuously re-challenge nodes on a randomized schedule. The re-verification cadence is designed so that no node goes more than 7 epochs without at least one independent re-verification check. The schedule is randomized and unpredictable to the node being challenged, preventing nodes from temporarily improving performance only during expected verification windows.

### Performance Metric Verification

Verifiers independently measure and attest to performance metrics that affect tier classification and reward calculations:

- Latency to the node from each verifier's location.
- Sustained bandwidth (upload and download) via timed transfers.
- IOPS (at node registration and on a slower periodic schedule thereafter).

Self-reported metrics that cannot be corroborated by verifier measurements are weighted lower in the reward calculation. Metrics that are consistently contradicted by verifier measurements cause the node's tier to be downgraded to the measured tier.

### Proof-of-Possession Challenges

Verifiers issue challenge-response tests to nodes to confirm they are actually storing the blobs they claim:

- The verifier selects a random blob from the node's on-chain storage manifest.
- The verifier requests a Merkle inclusion proof for a randomly selected byte range within that blob.
- The node must respond with the proof within a defined timeout (default: 30 seconds).
- A correct proof confirms the node currently holds the blob. Failure triggers a penalty stage entry (see [Node Runner Incentives and Penalties](./node-runner-incentives-and-penalties.md)).

---

## Verifier Eligibility

To become an eligible verifier, a node must meet all of the following requirements:

| Requirement | Value |
|---|---|
| Minimum stake | 5× the standard node stake minimum |
| Minimum tier classification | Tier 2 or above (verified Tier 1 preferred) |
| Minimum uptime in current epoch | ≥ 99% |
| Minimum uptime in trailing 30 epochs | ≥ 98% |
| Geographic distribution requirement | No more than 30% of the active verifier pool may be in the same region |
| Penalty stage | Must be Stage 0 (no active penalties) |
| On-chain registration | Must have submitted a verifier registration transaction and had it accepted by existing verifiers |

### Verifier Registration

A node registers as a verifier by:
1. Meeting all eligibility requirements.
2. Submitting a verifier registration transaction on-chain with its node public key, offered verifier services, and the additional stake deposit.
3. Being accepted by a majority of existing verifiers in a brief admission vote (any active verifier can object with evidence within 48 hours; without objection, the registration is confirmed automatically).

The admission vote is not a governance vote — it is a lightweight operational check to prevent Sybil registration and obviously ineligible nodes from joining the verifier pool.

---

## Verifier Selection for Challenges

When a node requires verification (new registration, re-verification, or proof-of-possession challenge), the protocol selects a verifier set using the following rules:

- **Minimum verifier set size**: 3 verifiers must participate and produce consistent results for an attestation to be accepted. At least 2 must agree for the result to be valid; a single dissenter can trigger an extended verification round but cannot alone invalidate a passing result.
- **Geographic diversity**: Verifiers are selected to maximize geographic diversity. At least 2 different regions must be represented in the verifier set for any region verification challenge.
- **No self-verification**: A node cannot be selected as a verifier for a challenge involving itself or any node it has a registered relationship with (same operator, same IP subnet, same ASN).
- **Randomized selection**: Verifiers are selected pseudorandomly from the eligible pool using a verifiable random function (VRF) seeded by the on-chain block hash at the time of challenge initiation. Selection is unpredictable to both the verifier and the node being challenged.

### Scaling with Network Size

The minimum verifier quorum scales with the size of the verifier pool to prevent a small cartel from controlling all verification:

| Verifier Pool Size | Minimum Verifiers Per Challenge |
|---|---|
| 3–10 | 3 |
| 11–30 | 5 |
| 31–100 | 7 |
| > 100 | 10 |

---

## Verifier Compensation

Verifiers are compensated separately from their storage node rewards:

- Each completed, on-chain-confirmed verification action earns a **verification fee** drawn from a dedicated verification fee pool. The fee pool is funded by a portion of each epoch's emission (a governance parameter, initially 2% of epoch emission).
- Verification fees are proportional to the type of work: region verification (more complex) pays more than a periodic heartbeat check or a simple proof-of-possession challenge.
- Verifiers who produce attestations that later prove to be incorrect (e.g., a verifier falsely attesting that a node is in a region when it is not) are subject to their own penalty stage progression and slash of the additional verifier stake.

### Fee Distribution

At epoch seal, each verifier's accumulated verification actions for the epoch are tallied from the on-chain record. Fees are distributed automatically via the same auto-payout mechanism as storage rewards (see [Node Runner Incentives and Penalties](./node-runner-incentives-and-penalties.md)).

---

## Verifier Misbehavior

Verifiers are trusted roles and are held to a higher standard than standard storage nodes.

| Violation | Penalty |
|---|---|
| Failing to respond to a verification assignment within the deadline | Missed assignment recorded; 3 misses in one epoch triggers Stage 1 entry |
| Producing an attestation inconsistent with the network consensus (outlier) | Warning logged; patterns of outlier attestations trigger Stage 2 entry |
| Confirmed fraudulent attestation (false region claim approved, or valid node rejected) | Immediate 25% stake slash, verifier status revoked, 60-day ban from re-registration |
| Colluding with the node being verified (documented evidence) | Immediate 50% slash, permanent ban from the verifier pool |

Verifier misbehavior penalties are applied to the **additional verifier stake** first, before touching the standard node stake. If the verifier stake is exhausted, penalties cascade to the standard stake.

---

## Verifier Rotation and Churn

- Verifiers may voluntarily exit the verifier pool by submitting a verifier exit transaction. There is a **30-epoch notice period** during which the verifier is expected to complete any pending verification assignments before their stake is returned.
- If a verifier's node falls below Tier 2 classification during the notice period, it is automatically downgraded to standard node status and removed from the verifier pool immediately. Pending assignments are reassigned.
- The total size of the verifier pool is not capped. Any node meeting the eligibility requirements may join. However, geographic distribution requirements prevent over-concentration in any single region.

---

## Foundation Verifiers

During Phase 0 and Phase 1 of network bootstrapping (see [Network Bootstrapping and Cold Start](./network-bootstrapping-and-cold-start.md)), foundation nodes serve as the initial verifier set. They are trusted by genesis and require no admission vote.

Foundation verifiers are phased out as the community verifier pool grows to a sufficient size and diversity. The foundation commits to maintaining at least 3 foundation verifiers until the community verifier pool reaches 15 independent verifiers across at least 3 regions.
