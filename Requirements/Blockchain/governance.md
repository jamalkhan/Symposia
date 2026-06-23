# Governance

## Overview

On-chain governance is the mechanism by which the protocol evolves without requiring trust in any single party. Token holders vote on proposed changes; approved changes are executed automatically after a time-lock. No single wallet, team, or organization can unilaterally change protocol behavior — every change requires a transparent, stakeholder-driven process.

This file defines the governance mechanics: who can propose, how voting works, what quorum is required, how approvals become code, and how the system transitions from bootstrap (multi-sig) control to full decentralized governance.

---

## Bootstrap Governance Phase

At mainnet launch, full decentralized governance is not yet feasible — the token is not broadly distributed and there are no established token holders with deep protocol knowledge. During this phase, a **founding multi-sig council** acts as a proxy for on-chain governance.

### Multi-Sig Structure

- A **3-of-5 multi-sig** controls all governance functions at launch.
- The 5 keyholders are drawn from the founding team, with a design that ensures no single individual holds more than one key.
- All multi-sig signers are publicly identified. Their wallet addresses are published in the genesis documentation and on-chain.
- The multi-sig can propose and approve any governance action, subject to the same time-lock as full on-chain governance (see Time-Lock below).

### Transition Timeline

The multi-sig is a bootstrap mechanism, not a permanent governance model. The transition to full on-chain governance follows this schedule:

| Milestone | Trigger |
|---|---|
| First governance proposal submitted by a non-founding token holder | Network has been live for ≥ 60 days AND at least 500 distinct non-foundation wallets hold ≥ 100 tokens each |
| Multi-sig veto power removed | 6 months post-mainnet AND the token is listed on at least one liquid exchange |
| Multi-sig dissolved | 12 months post-mainnet, by a supermajority governance vote |

After dissolution, the multi-sig is replaced entirely by on-chain token-holder voting. No keyholders retain special authority.

---

## Proposal Types

Not all governance changes carry the same risk. The governance system defines three proposal tiers with different thresholds and time-locks:

### Tier 1 — Parameter Adjustment

Low-risk changes to numeric parameters within predefined safe ranges.

Examples: adjusting reward factor weights, changing the epoch finality window length, updating tier classification thresholds, adjusting the multipart upload TTL default.

- Any token holder above the proposal threshold may submit.
- Standard quorum and approval threshold.
- Standard time-lock.

### Tier 2 — Structural Change

Higher-risk changes to protocol logic, contract interfaces, or region definitions.

Examples: adding or retiring a region, changing the slashing formula, adding a new supported stablecoin, modifying the verifier selection algorithm.

- Requires a higher proposal stake to deter spam.
- Higher quorum and approval threshold than Tier 1.
- Extended time-lock.

### Tier 3 — Critical Change

The highest-risk changes: contract upgrades, chain migration votes, changes to the token supply cap or emission schedule, and governance rule changes themselves.

Examples: approving a migration from L3 to standalone L1, upgrading a core smart contract, changing the total supply cap, amending the governance rules in this document.

- Requires the highest proposal stake.
- Supermajority threshold required.
- Maximum time-lock.
- May not be submitted more than once in any 90-day window for the same subject.

---

## Proposal Lifecycle

### 1. Submission

A governance proposal is submitted on-chain with:
- **Proposal type** (Tier 1 / 2 / 3).
- **Title and description**: Human-readable explanation of what is being changed and why.
- **Executable payload**: The exact on-chain action to execute if approved (e.g., a contract call with specific parameters, a configuration update, a transaction).
- **Proposer stake**: The required token stake, which is locked for the duration of voting. This disincentivizes frivolous proposals; the stake is returned if the proposal meets the quorum threshold (whether it passes or fails), and forfeited if the proposal fails to reach quorum.

| Tier | Proposer Stake |
|---|---|
| Tier 1 | 0.1% of circulating supply |
| Tier 2 | 0.5% of circulating supply |
| Tier 3 | 1.0% of circulating supply |

### 2. Discussion Period

After submission, a **mandatory discussion period** precedes voting. This window allows the community to review, ask questions, and raise concerns before votes are cast.

| Tier | Discussion Period |
|---|---|
| Tier 1 | 3 days |
| Tier 2 | 7 days |
| Tier 3 | 14 days |

No voting occurs during the discussion period. The proposal may be withdrawn by the proposer at any time during this period (stake is returned in full on voluntary withdrawal before voting begins).

### 3. Voting Period

After the discussion period, the voting window opens. Token holders vote with their tokens:

- **Vote options**: For, Against, Abstain.
- **Voting weight**: One token = one vote. Tokens are not locked during voting; the vote weight is snapshotted at the block when the voting period opens. Tokens transferred after the snapshot do not change vote weights.
- **Delegation**: Token holders may delegate their voting power to another address. Delegation is revocable at any time.

| Tier | Voting Period |
|---|---|
| Tier 1 | 5 days |
| Tier 2 | 7 days |
| Tier 3 | 14 days |

### 4. Quorum and Approval Thresholds

For a proposal to pass, both the quorum threshold and the approval threshold must be met:

| Tier | Quorum (of circulating supply) | Approval (of votes cast) |
|---|---|---|
| Tier 1 | 5% | > 50% For |
| Tier 2 | 10% | > 60% For |
| Tier 3 | 20% | > 75% For |

**Quorum**: The percentage of total circulating supply that must participate (For + Against + Abstain). If quorum is not reached, the proposal fails regardless of the For/Against ratio, and the proposer's stake is forfeited.

**Approval**: Of the votes actually cast, the percentage that must be For. Abstain votes count toward quorum but not toward approval.

### 5. Time-Lock

After a proposal passes, it enters a **time-lock period** before the executable payload is applied. This gives participants who disagree with the outcome time to exit positions before the change takes effect.

| Tier | Time-Lock |
|---|---|
| Tier 1 | 2 days |
| Tier 2 | 5 days |
| Tier 3 | 14 days |

The time-lock may be cancelled only by a subsequent emergency governance action (see Emergency Actions below). Once the time-lock expires, the payload is executed automatically by the governance contract — no human intervention is required or permitted.

### 6. Execution

The governance contract executes the payload atomically at the end of the time-lock. If the execution reverts (e.g., a contract call fails because on-chain state has changed), the proposal is marked as failed without retry. A new proposal must be submitted.

---

## Veto Mechanism (Bootstrap Phase Only)

During the bootstrap governance phase, the founding multi-sig retains a **veto right** over any passed proposal. The veto may be exercised within the time-lock window. Veto is intended only for proposals that would actively harm the network (e.g., a governance attack, a critical security flaw in the proposed change).

Every veto requires:
- All 5 multi-sig keyholders to be publicly notified.
- A written explanation published on-chain and in community channels.
- A 48-hour waiting period after the explanation before the veto is finalized.

Veto power is removed per the transition timeline above. After removal, no entity has override authority over passed proposals.

---

## Emergency Actions

For time-critical security situations (e.g., a critical vulnerability in a core contract that is actively being exploited), the governance system includes a fast-track emergency process:

- **Eligible proponents**: The multi-sig (during bootstrap phase) or a committee designated by governance.
- **Emergency quorum**: 15% of circulating supply, gathered within 24 hours.
- **Time-lock**: Shortened to 6 hours.
- **Scope**: Emergency actions may only pause affected contracts or revert a recently executed change. They may not introduce new features or alter token distribution.

An emergency action that does not reach 15% quorum within 24 hours expires automatically and has no effect. Emergency actions that are used for non-emergency purposes are subject to governance removal of the committee and forfeiture of proposer stake.

---

## Governable Parameters

The following is an enumeration of parameters that are subject to on-chain governance. All other protocol behaviors require a contract upgrade (Tier 3 proposal).

### Network Parameters (Tier 1)
- Epoch duration
- Heartbeat submission interval
- Epoch finality window
- Node health cache TTL
- Write timeout

### Reward and Penalty Parameters (Tier 1)
- Reward factor weights (uptime, latency, I/O, bandwidth, retrieval speed)
- Performance tier reward multipliers
- Disincentive period length and severity
- Slash percentages per stage (within defined safe ranges)
- Auto-payout heartbeat threshold (currently 90%)

### Network Topology Parameters (Tier 1–2)
- Minimum verifier quorum for region verification
- Fault domain soft rule weights
- Minimum replication factor (Tier 1 if increasing; Tier 2 if decreasing)

### Region Definitions (Tier 2)
- Adding a new region
- Retiring an existing region
- Renaming a region identifier

### Economic Parameters (Tier 2)
- Minimum node stake per unit of capacity
- Staking cooldown period
- Supported stablecoin list
- Base storage and egress rates

### Critical Parameters (Tier 3)
- Token total supply cap
- Epoch emission schedule
- Core contract upgrades
- Chain migration vote (L3 to standalone L1)
- Governance rules themselves
