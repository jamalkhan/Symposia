# Chain Architecture

## Overview

This file describes the technical architecture of the protocol's blockchain layer: how it is built, how it settles, how it handles finality, and how it migrates from its initial form (an L3 on Base) to a standalone L1 in the future. This is the engineering specification that sits beneath the economic model described in Blockchain and Tokenomics requirements.

---

## Current Architecture: OP Stack L3 on Base

### What This Means

The protocol's chain is a **Layer 3 (L3)** built using the **OP Stack** — the same open-source framework that Base itself is built on. The chain settles its transaction batches to **Base (L2)**, which in turn settles to **Ethereum (L1)**.

The settlement hierarchy:

```
Protocol L3  →  Base (L2)  →  Ethereum (L1)
   (us)         (Coinbase)     (canonical)
```

This means:
- The L3 is EVM-compatible: all smart contracts are standard Solidity, all tooling (Hardhat, Foundry, ethers.js, viem, wagmi) works without modification.
- All Ethereum-compatible wallets (MetaMask, Coinbase Wallet, Ledger, Rainbow) support the L3 natively — users add a custom RPC endpoint and the token appears.
- The chain inherits Base's security and fraud proof system, which inherits Ethereum's.
- Deploying on Base's ecosystem gives immediate access to existing liquidity, DEX infrastructure, and bridge tooling.

### Why L3 on Base (Not a Custom L1)

At launch, the network will have very few nodes and very low transaction volume. Building and securing a standalone L1 at this stage would consume engineering resources better spent on the storage protocol itself and would require the chain to bootstrap its own validator set and security model from scratch. The L3 on Base approach provides production-grade security from day one at a fraction of the engineering cost.

The architecture is explicitly designed so that **migrating to a standalone L1 is a deployment operation, not a rewrite** — see Migration Path below.

---

## Data Availability

Transaction data (the input data required to reconstruct the L3 chain from scratch) is posted to **Base** using **EIP-4844 blobs**.

- EIP-4844 blobs are a dedicated data posting mechanism on Ethereum/Base that is significantly cheaper than calldata while providing the same security guarantees.
- The L3 sequencer batches L3 transactions and posts them to Base periodically. Nodes and anyone else can reconstruct the full L3 chain state from these blobs.
- At early network transaction volumes (hundreds to low thousands of transactions per day), DA costs are negligible — on the order of dollars per year.
- The OP Stack supports switching to an alternative DA layer (Celestia, EigenDA) via a configuration change ("alt-DA mode") if costs become a concern at scale. This does not require contract changes or client software updates.

---

## Sequencer

The sequencer is the component that receives L3 transactions, orders them, and submits batches to Base.

### Phase 1 — Centralized Sequencer (Launch)

At launch, the sequencer is operated by the platform organization as a single, trusted entity. This is standard practice for all OP Stack chains at launch, including Base itself at its inception.

- The sequencer is the only entity authorized to produce L3 blocks.
- The sequencer is responsible for liveness (if it goes down, the L3 stops producing blocks until it recovers). This is an accepted risk at early stages.
- Transactions are censorship-resistant via a **force inclusion** mechanism: if the sequencer refuses to include a transaction for 24 hours, any user can force-include it directly via the Base L2. This prevents the sequencer from censoring users even in the centralized phase.
- The sequencer's private key is held in a hardware security module (HSM) and is not accessible to individual team members without multi-party authorization.

### Phase 2 — Decentralized Sequencer (Future)

A governance vote will trigger the transition to a decentralized sequencer set. The OP Stack's decentralized sequencer roadmap (developed by Optimism) will be adopted when it reaches production maturity. The transition does not require changes to smart contracts or client software.

Candidates for decentralized sequencing include:
- **OP Stack native**: Optimism's own decentralized sequencer protocol, currently in development.
- **EigenLayer AVS**: Using EigenLayer's restaking mechanism to create a decentralized sequencer set secured by restaked ETH.

The governance vote to transition will include the specific mechanism, timeline, and sequencer operator requirements.

---

## Finality Model

The OP Stack uses an **optimistic rollup** model. There are two distinct finality states:

### Soft Finality (Seconds)

A transaction achieves soft finality the moment the sequencer includes it in an L3 block and that block is gossiped to the network. From the user's perspective this is effectively instant — typically 1–2 seconds.

**All storage protocol operations use soft finality:**
- Blob deal creation (tenant uploads confirmed).
- Node registration and region verification attestations.
- Epoch reward records.
- Slash events.
- Governance proposals and votes.

Nodes begin serving a blob as soon as the deal reaches soft finality. The theoretical ability for a transaction to be reversed before hard finality is accepted as a negligible risk — an invalid block posted by the sequencer can be challenged and reversed, but this would be a catastrophic sequencer failure, not a routine event.

### Hard Finality (7 Days)

A transaction achieves hard finality after the **7-day fraud proof window** closes on Base. During this window, any party can submit a fraud proof to Base proving the L3 sequencer included an invalid transaction. After 7 days with no successful fraud proof, the transaction is irrevocably settled.

**Token bridge withdrawals require hard finality:**
- Moving tokens from the L3 to Base or Ethereum mainnet requires waiting 7 days.
- This is the standard UX for all OP Stack chains and is well understood by the Ethereum ecosystem.
- Within the L3 itself, tokens are usable immediately at soft finality — the 7-day window only applies to cross-chain bridge withdrawals.

### Fast Withdrawal Options (Future)

Third-party "fast withdrawal" services (liquidity providers who front tokens on Base in exchange for a fee and collect the bridged tokens after 7 days) can reduce the withdrawal UX to minutes for users willing to pay a small fee. These are provided by third parties and require no changes to the L3 protocol.

---

## Token Bridge Architecture

The native token (ERC-20) exists in three forms simultaneously:

| Location | Form | How to Get There |
|---|---|---|
| **L3 (Protocol chain)** | Native ERC-20 | Earned via node rewards, purchased via DEX on L3, bridged from Base |
| **Base (L2)** | Bridged ERC-20 | Bridge from L3 (7-day delay) or purchased on Base DEX |
| **Ethereum Mainnet** | Bridged ERC-20 | Bridge from Base (another 7-day delay for L1 finality) |

The canonical token lives on the L3. Base and Ethereum representations are bridged copies. The canonical OP Stack bridge handles all bridging and is battle-tested across the OP Stack ecosystem.

---

## ERC-20 Compatibility

The native token is a standard ERC-20 contract with no proprietary extensions that would break wallet or DEX compatibility. Specifically:

- Implements the full ERC-20 interface: `transfer`, `transferFrom`, `approve`, `allowance`, `balanceOf`, `totalSupply`.
- Implements ERC-20 Permit (EIP-2612) for gasless approvals — users can approve spending without a separate transaction.
- Implements ERC-20 Votes (EIP-5805) to enable on-chain governance voting directly from token balances without requiring a separate delegation transaction.
- No transfer hooks, no blacklisting, no admin mint functions beyond the defined emission schedule contract. The token contract is simple and auditable.

---

## Migration Path: L3 → Standalone L1

The architecture is designed from day one so that migrating from an L3 on Base to a standalone L1 is an orchestrated deployment operation, not an engineering rewrite.

### What Doesn't Change

- All smart contracts are standard EVM Solidity. They redeploy to the new L1 without modification.
- The node software connects to the chain via a standard EVM RPC endpoint. Switching chains is a configuration change (new RPC URL).
- The token remains ERC-20 compatible on the new chain.
- Tenant-facing APIs and SDK behavior do not change.

### What the Migration Involves

1. **Governance vote**: A formal on-chain governance proposal is approved with a supermajority and time-lock.
2. **New chain deployment**: The standalone L1 is deployed with its own validator set, consensus mechanism, and genesis block.
3. **Token migration contract**: A "lock and mint" bridge contract is deployed. Users lock their L3 tokens and receive equivalent tokens on the L1. A migration window (e.g., 12 months) is provided before the L3 is deprecated.
4. **Smart contract redeployment**: All protocol contracts (node registry, blob deals, reward distribution, staking, governance) are redeployed on the L1 with the same ABIs.
5. **Node software update**: A node software release points to the new L1 RPC endpoint. Existing nodes update their config and restart.
6. **Historical data**: L3 chain history is archived and remains queryable, but new activity settles on L1. On-chain records (node registrations, blob deals, reward history) are migrated via a snapshot-and-replay mechanism at migration block.
7. **L3 deprecation**: After the migration window closes, the L3 is deprecated. The canonical bridge between L3 and L1 remains open for the duration of the migration window.

### Migration Prerequisites

The migration should not be triggered until:
- The network has sufficient node count and stake to secure a standalone validator set (minimum: a governance-defined threshold, e.g., 100 independent validators).
- The token has sufficient market liquidity on both the L3 and the new chain to support the transition without large price disruption.
- The L1 consensus mechanism and validator onboarding have been tested on a public testnet for a minimum of 90 days.
- A third-party security audit of the token migration contract has been completed.

### L1 Consensus (Future Decision)

The specific consensus mechanism for the standalone L1 is a future decision to be made when the migration is approaching. Candidates include Tendermint/CometBFT (used by Cosmos chains), a custom PoS mechanism, or adopting an emerging modular consensus framework. This decision does not affect the current L3 architecture.
