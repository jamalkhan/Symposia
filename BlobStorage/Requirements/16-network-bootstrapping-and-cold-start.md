# Network Bootstrapping and Cold Start

## Overview

A decentralized storage network faces a fundamental chicken-and-egg problem at launch: storage nodes need tenants to justify running hardware, and tenants need a functioning node network to store data. Neither side joins first without confidence the other will be there. This file defines the strategy for breaking that deadlock and building a network that can sustain itself once it reaches critical mass.

---

## The Cold Start Problem

The specific failure modes to plan for:

- **No nodes, no storage**: If the network launches with zero node runners, tenants cannot upload data. The first tenants have no reason to trust the network.
- **No tenants, no revenue**: If node runners join but there are no tenant uploads, nodes earn nothing. Operators turn off their hardware.
- **No tokens, no staking**: Node runners must stake tokens to participate. If the token has no liquid market yet, acquiring tokens to stake is a barrier to entry.
- **No verification, no rewards**: The region verification system requires multiple verifier nodes to be online before any new node can be verified. A minimal network cannot verify itself.

---

## Phase 0 — Foundation Infrastructure (Pre-Launch)

Before any public access is opened, the platform operator runs a set of **foundation nodes** across multiple geographic regions. These nodes:

- Are operated directly by the organization.
- Are pre-staked using tokens from the foundation allocation (see Token Distribution requirements).
- Serve as the initial verifier set for region verification.
- Provide enough storage capacity to onboard the first wave of tenants.
- Remain operational for a minimum of 12 months post-mainnet, regardless of network growth, to ensure baseline availability.
- Do not earn token rewards that exceed their operational costs — surplus rewards from foundation nodes are returned to the ecosystem reserve.

Foundation nodes are documented publicly. Their existence is not hidden — tenants and the community know they exist and why. The goal is to phase them out as the community-operated node network grows.

---

## Phase 1 — Private Testnet (Internal + Partners)

- The full stack runs on testnet infrastructure.
- A small cohort of invited node runners operates nodes and receives test token rewards to validate the incentive mechanics.
- A small cohort of invited tenants stores real data (on testnet) to validate the storage, replication, and billing mechanics.
- All bugs found in the epoch mechanics, region verification, and replication systems are fixed before proceeding.
- Duration: until the system passes a defined stability bar (e.g., 30 consecutive days without a network-level incident).

---

## Phase 2 — Public Testnet

- Testnet opens to all node runners and developers.
- Test tokens are freely available via a faucet (request tokens by submitting a node public key or developer wallet address).
- Node runners can onboard, run the full benchmark suite, get region verified, and earn test rewards.
- Developers can integrate their applications against the S3/Azure interfaces.
- Testnet data is periodically purged (every 30 days, announced in advance).
- A **node runner leaderboard** publishes uptime, tier classification, and test earnings for all nodes. This builds social proof and a community of operators before mainnet.
- Duration: minimum 60 days. Extended until the network sustains a defined node count across a defined number of regions (e.g., ≥50 nodes across ≥5 regions) for 30 consecutive days.

---

## Phase 3 — Mainnet Launch

### Supply-Side (Node Runners)

- **Genesis staking program**: Node runners who commit to operating for a minimum period (e.g., 6 months) and meet Tier 2 or higher classification receive a **genesis multiplier** on their epoch rewards for the first 90 days of mainnet. This rewards early risk-taking.
- **Reduced staking requirement**: For the first 90 days, the minimum stake required to join is 50% of the long-term requirement, lowering the barrier to entry while the token market matures.
- **Onboarding support**: Dedicated support channel for genesis node runners experiencing setup issues.
- The network does not open to public tenant traffic until a minimum supply threshold is met: e.g., ≥100 nodes across ≥3 regions, enough capacity to satisfy the zero-region copy rules with meaningful geographic diversity.

### Demand-Side (Tenants)

- **Free tier**: Each new tenant receives a small credit allocation (e.g., 3 months of modest storage at Tier 3) with no payment information required. This removes the commitment barrier for initial exploration.
- **Migration credits**: Tenants migrating documented workloads from S3 or Azure receive matching credits for their first 3 months, incentivizing real workload migration rather than toy projects.
- **Early access partners**: A cohort of design partners (developers, companies) is identified during testnet and given early mainnet access with dedicated support. Their feedback and public use cases provide social proof.
- **Developer program**: A formal program recognizing developers who build publicly on the platform, with increased credits, early feature access, and co-marketing.

---

## Critical Mass Definition

The network is considered to have passed the cold start risk once it sustains all of the following for 30 consecutive days without foundation node intervention:

- ≥ 200 independently operated nodes.
- ≥ 5 distinct geographic regions with ≥ 10 nodes each.
- ≥ 50 active paying tenants with real stored data.
- Zero epochs where the network could not satisfy minimum replication requirements for new writes.
- Token available on at least one liquid exchange, enabling node runners to acquire stake and liquidate rewards.

Once critical mass is reached, foundation nodes begin a phased wind-down over 6 months.

---

## Bootstrap Verification Problem

Region verification requires multiple verifiers. In a minimal network, there may not be enough verified nodes to form a quorum for verifying new ones. This is resolved as follows:

- Foundation nodes serve as the initial verifier set and are trusted by genesis.
- As community nodes pass verification, they are added to the eligible verifier pool.
- The minimum verifier quorum required to verify a new node scales with network size: it starts at 3 (achievable with foundation nodes alone) and increases as the verifier pool grows, up to the long-term target.
- The quorum formula and growth schedule are defined in the chain's genesis configuration.
