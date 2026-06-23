# Region Identification and Verification

## Overview

Region is a core primitive of the network. Every node must belong to a verified region. This requirement is two-sided: nodes self-declare their region, and the network independently verifies that declaration using measurable network characteristics. Nodes that cannot be verified are excluded from the reward pool and from receiving new storage assignments.

## Region Definitions

- Regions are hierarchical and well-defined: e.g., `us-east`, `eu-west`, `apac-sg`.
- Finer-grained metro or city-level regions may be defined as the network matures.
- The canonical region list is governed on-chain; new regions require a governance vote.
- Each node belongs to exactly one region at any given time.

## Self-Identification

- At registration, a node submits a **signed region claim**: a statement asserting its physical location (region), accompanied by supporting evidence (IP geolocation data, AS number, timestamps, and operator-provided attestation).
- The claim is signed by the node's private key, binding it to the node's identity.
- A node that cannot produce a coherent and consistent self-identification claim is not eligible for region verification.

## Network Verification

- A subset of existing, already-verified nodes act as **verifiers** for new claims. See [Verifier Nodes](./verifier-nodes.md) for eligibility requirements, selection mechanics, compensation, and misbehavior penalties.
- Verifiers measure the following from multiple geographic vantage points against the claimant:
  - Round-trip latency (ping)
  - Jitter (variance in latency)
  - Hop count (traceroute depth)
  - Packet loss rate
  - Throughput (bandwidth test)
- Measurements from multiple verifiers are aggregated. Statistical outlier rejection is applied.
- The aggregated measurements are compared against expected ranges for the claimed region using a latency triangulation model (inspired by approaches such as Witness Chain's Proof of Location).
- If the measurements are consistent with the claimed region within defined tolerance thresholds, the claim is **verified**. The outcome is recorded on-chain as an attestation signed by the participating verifiers.
- If measurements are inconsistent (e.g., claimed `eu-west` but latency profile matches `us-east`), the claim is **rejected**. The node is marked unverified and excluded from earning tokens.

## Re-Verification

- Region verification is not a one-time event. Nodes are subject to **periodic re-verification challenges** on a randomized schedule.
- A node that passes re-verification has its on-chain attestation renewed.
- A node that fails re-verification is immediately suspended from new storage assignments and token rewards until it re-establishes verification.
- Repeated failures may result in slashing of staked collateral (see [Blockchain and Tokenomics](../Blockchain/blockchain-and-tokenomics.md)).

## Sybil and Spoofing Resistance

- A node cannot register multiple identities with the same network characteristics; the verification process must detect and reject duplicate nodes presenting the same latency fingerprint.
- VPN/proxy tunneling to fake a region will result in characteristic latency signatures that are inconsistent with the claimed location and will fail verification.
- Zero-knowledge proofs of location may be layered on top of the latency model in future versions to add privacy without sacrificing verifiability.

## Governance and Region Updates

- A node may request a region update (e.g., if it physically moves to a new data center). The update triggers a fresh verification round and a disincentive period while the move is validated.
- The on-chain record of each node's current and historical region claims and verification outcomes is publicly auditable.
