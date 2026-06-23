# Blockchain and Tokenomics

## Overview

The system is backed by a purpose-built blockchain and native token. The blockchain serves as the coordination, trust, and incentive layer for the entire storage network. It is not a general-purpose chain — its design is optimized for the specific needs of decentralized blob storage.

The chain launches as a **Layer 3 (L3) on top of Base** (Coinbase's L2), using the OP Stack. This provides immediate EVM compatibility, existing wallet support, and low operational overhead at launch, while preserving a clear migration path to a standalone Layer 1 in the future. See [Chain Architecture](./chain-architecture.md) for full technical detail.

## The Blockchain

- The chain is an **OP Stack L3 settling to Base**. It is EVM-compatible and inherits Base's security model, which in turn inherits Ethereum's.
- The chain records:
  - Node registrations, region claims, and verification attestations.
  - Blob metadata (content hash/CID, size, region assignments, replica node list, deal terms).
  - Token minting, transfers, reward distributions, and slashing events.
  - Governance proposals and outcomes.
- **Finality model**: Storage operations (blob deals, node registrations, reward records) use **soft finality** — transactions are considered settled as soon as they are ordered by the sequencer, which takes seconds. Token withdrawals from the L3 to Base or Ethereum use **hard finality**, which requires the 7-day optimistic fraud proof window to close. This is the standard UX for all OP Stack chains and is acceptable for this use case.
- Light client support is required so that storage nodes can verify chain state without running a full node.
- The chain is designed so that the settlement layer (Base) can be replaced with a purpose-built standalone L1 in the future, without requiring changes to the smart contracts or node software. See [Chain Architecture](./chain-architecture.md) for the migration path.

## The Native Token

- The native token is an **ERC-20 token** deployed on the L3. ERC-20 compatibility ensures that all Ethereum-compatible wallets (MetaMask, Coinbase Wallet, Rainbow, Ledger, etc.) support it natively with no custom integration required.
- The token is bridgeable between the L3, Base, and Ethereum mainnet via the canonical OP Stack bridge.
- The token has three primary utilities:
  1. **Payment**: Tenants pay for storage and retrieval in the token (or a supported stablecoin, auto-swapped at settlement).
  2. **Staking**: Node operators stake tokens as collateral to participate and earn rewards.
  3. **Governance**: Token holders vote on protocol parameters (region definitions, reward weights, slashing conditions, etc.).
- Token supply, emission schedule, and inflation parameters are defined in the Token Distribution and Launch Economics requirements. The architecture must support configurable emission and not hardcode values.

## User Payment Model

- Tenants pay on a **per-byte-per-epoch** basis for storage and a **per-GB** basis for egress. Pricing varies by performance tier and region.
- Payment is prepaid via credits purchased in the native token or a supported stablecoin.
- Pricing is transparent and queryable via the API before committing storage.
- Non-payment follows a defined schedule: a grace period, then soft suspension, then soft delete, then hard delete. Data is never immediately destroyed on non-payment.

Full detail on pricing, credit mechanics, non-payment schedules, and tenant-controlled retention policies is in [Data Retention and Billing](../Platform/retention-and-billing.md).

## Node Staking and Collateral

- Storage nodes must stake a minimum amount of tokens to register and participate.
- The stake scales with offered storage capacity: more capacity requires more stake.
- The stake is the collateral subject to slashing (see [Node Runner Incentives and Penalties](../Network/node-runner-incentives-and-penalties.md)).
- Staking requirements are set by governance and may change over time.

## Smart Contract Architecture

- Core protocol logic (node registry, blob deals, reward distribution, slashing) is implemented as smart contracts deployed on the L3.
- All contracts are EVM-compatible Solidity contracts, deployable without modification to any EVM chain — including a future standalone L1.
- Off-chain components (performance metrics aggregation, verification measurement) feed into on-chain state via signed reports or oracle mechanisms with on-chain roots.
- Contract upgradability must be governed: no single party can unilaterally change contract logic; upgrades require a governance vote and time-lock.

## Governance

- On-chain governance allows token holders to propose and vote on protocol changes including:
  - Reward weight adjustments (how uptime, latency, I/O, etc. are weighted).
  - Minimum replication factor changes.
  - Region definitions (adding, renaming, retiring regions).
  - Slashing condition and severity changes.
  - Fee adjustments.
  - DA layer changes (e.g., migrating from Base DA to an alt-DA layer).
  - Chain migration votes (approving the move from L3 to standalone L1).
- Proposals have a defined voting period and a time-lock between approval and activation.
- Bootstrap governance is a **multi-sig council** (e.g., 3-of-5 keyholders from the founding team) with a planned transition to full on-chain token-holder governance once the token is sufficiently distributed.

See [Governance](./governance.md) for full mechanics: proposal tiers, voting periods, quorum thresholds, time-locks, the veto mechanism, and the bootstrap-to-decentralized transition timeline.
