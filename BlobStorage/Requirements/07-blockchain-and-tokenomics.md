# Blockchain and Tokenomics

## Overview

The system is backed by a purpose-built blockchain and native token. The blockchain serves as the coordination, trust, and incentive layer for the entire storage network. It is not a general-purpose chain — its design is optimized for the specific needs of decentralized blob storage.

## The Blockchain

- A new blockchain and corresponding token are designed from the ground up for this protocol. No existing chain is assumed.
- The chain records:
  - Node registrations, region claims, and verification attestations.
  - Blob metadata (content hash/CID, size, region assignments, replica node list, deal terms).
  - Token minting, transfers, rewards distributions, and slashing events.
  - Governance proposals and outcomes.
- The chain must provide **finality** within a time window that is practical for storage operations (e.g., confirmation of a new blob deal within seconds to a few minutes).
- Light client support is required so that storage nodes can verify chain state without running a full node if desired.
- The chain is fully custom-built for this protocol. It does not piggyback on Ethereum, an L2, or any existing chain. All consensus, execution, and settlement logic is purpose-built.

## The Native Token

- The native token has three primary utilities:
  1. **Payment**: Users pay for storage and retrieval in the token (or in a stablecoin pegged/swapped to it).
  2. **Staking**: Node operators stake tokens as collateral to participate and earn rewards.
  3. **Governance**: Token holders vote on protocol parameters (region definitions, reward weights, slashing conditions, etc.).
- Token supply, emission schedule, and inflation parameters are to be defined in a separate Tokenomics specification. The architecture must support configurable emission and not hardcode values.

## User Payment Model

- Tenants pay on a **per-byte-per-epoch** basis for storage and a **per-GB** basis for egress. Pricing varies by performance tier and region.
- Payment is prepaid via credits purchased in the native token or a supported stablecoin.
- Pricing is transparent and queryable via the API before committing storage.
- Non-payment follows a defined schedule: a grace period, then soft suspension, then soft delete, then hard delete. Data is never immediately destroyed on non-payment.

Full detail on pricing, credit mechanics, non-payment schedules, and tenant-controlled retention policies is in the Data Retention and Billing requirements.

## Node Staking and Collateral

- Storage nodes must stake a minimum amount of tokens to register and participate.
- The stake scales with offered storage capacity: more capacity requires more stake.
- The stake is the collateral subject to slashing (see Incentives & Penalties requirements).
- Staking requirements are set by governance and may change over time.

## Smart Contract Architecture

- Core protocol logic (node registry, blob deals, reward distribution, slashing) is implemented as smart contracts on the chain.
- Off-chain components (performance metrics aggregation, verification measurement) feed into on-chain state via signed reports or oracle mechanisms with on-chain roots.
- Contract upgradability must be governed: no single party can unilaterally change contract logic; upgrades require a governance vote and time-lock.

## Governance

- On-chain governance allows token holders to propose and vote on protocol changes including:
  - Reward weight adjustments (how uptime, latency, I/O, etc. are weighted).
  - Minimum replication factor changes.
  - Region definitions (adding, renaming, retiring regions).
  - Slashing condition and severity changes.
  - Fee adjustments.
- Proposals have a defined voting period and a time-lock between approval and activation.
- Bootstrap governance may be more centralized initially (e.g., multi-sig council) with a planned transition to full on-chain governance.
