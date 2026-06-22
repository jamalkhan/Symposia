# Token Distribution and Launch Economics

## Overview

The native token is the economic backbone of the network. Its initial distribution determines who has a stake in the network's success, how decentralized governance is from day one, and whether the token can function as a practical payment and staking mechanism. Poor initial distribution — too concentrated, too opaque, or with badly designed vesting — can permanently damage a network's credibility and decentralization.

This document defines the framework and principles for token distribution. Exact quantities, emission curves, and pricing are to be finalized in a separate Tokenomics specification developed with input from economic advisors and the community.

---

## Total Supply

- The total token supply is fixed at genesis. No additional tokens can be minted beyond the defined emission schedule.
- The supply cap and emission schedule are encoded in the genesis block and are not changeable without a governance vote with a supermajority threshold.
- The supply must be large enough that individual token amounts remain human-readable at realistic per-unit prices, and small enough to avoid psychological perception of worthlessness. The exact figure is a Tokenomics spec decision.

---

## Allocation Categories

The total supply is divided across the following categories. Percentages are illustrative and subject to the Tokenomics specification.

| Category | Illustrative % | Purpose |
|---|---|---|
| **Network Rewards (Emission)** | 40% | Released over time via the epoch reward mechanism to node runners. Never pre-distributed; earned by contributing to the network. |
| **Ecosystem and Grants** | 15% | Developer grants, integration partnerships, hackathons, open-source contributions. Managed by a foundation or DAO. |
| **Foundation Reserve** | 10% | Operational runway for the organization building and maintaining the protocol. Long vesting; see below. |
| **Early Contributors and Team** | 15% | Compensation for the team that built the network. Long vesting; see below. |
| **Investors** | 10% | Seed and early-stage investors. Vesting; see below. |
| **Public Launch / Community** | 10% | Distributed at launch via public sale, airdrop, or testnet participant rewards. Provides immediate liquidity and broad initial distribution. |

### Key Principle

The Network Rewards allocation is the largest single bucket and is earned, not distributed. This means the majority of tokens flow to people actively contributing to the network, not to insiders holding from genesis.

---

## Vesting Schedules

All insider allocations (team, foundation, investors) are subject to vesting to prevent immediate sell pressure and to align long-term incentives.

| Category | Cliff | Vesting Duration | Notes |
|---|---|---|---|
| **Team / Early Contributors** | 12 months | 48 months total (linear after cliff) | No tokens accessible until 1 year post-mainnet. |
| **Foundation Reserve** | 6 months | 36 months total | Shorter cliff acknowledges operational needs; still long-term aligned. |
| **Investors** | 12 months | 36 months total | Standard for early-stage crypto investment. |
| **Ecosystem / Grants** | None | Deployed over 5+ years at foundation/DAO discretion | No cliff; grants are disbursed as awarded, not held by insiders. |

All vesting schedules are encoded on-chain and enforced by smart contract. There is no off-chain override, no discretionary early release, and no ability for any single party to accelerate vesting.

---

## Public Launch Mechanism

The public launch allocation may be distributed via one or more of:

- **Public token sale**: Tokens sold at a published price with a per-wallet purchase cap to prevent concentration.
- **Testnet participant rewards**: Node runners and tenants who participated in the public testnet receive a token allocation based on their verified contribution (uptime, data stored, etc.). This rewards early risk-takers who built the network before it had value.
- **Community airdrop**: Broad distribution to a defined eligible community (e.g., users of related protocols, early waitlist members). Airdrops use a merkle tree claim mechanism; unclaimed tokens after a claim window (e.g., 6 months) revert to the ecosystem reserve.

The specific mechanism(s) and timeline are a Tokenomics specification decision.

---

## Staking and Liquidity

### Node Runner Staking Supply

Node runners must stake tokens to participate. For staking to be practical at launch, tokens must be available to acquire before or at mainnet launch — this is why the public launch allocation is distributed before or concurrent with mainnet opening to node runners.

- The minimum stake per node is set at a level achievable with the tokens available at launch, not at a future speculative price.
- A **stake-to-earn ratio** is defined: the expected time to recover the staked amount from epoch rewards, at baseline performance. This must be publicly communicated before mainnet so node runners can evaluate the economics.

### Exchange Listing

- The token must be listed on at least one liquid exchange at or before mainnet launch. Node runners and tenants need the ability to acquire tokens (to stake or pay for storage) and liquidate earnings.
- The foundation pursues exchange listings as a pre-launch requirement, not a post-launch afterthought.
- Stablecoins are accepted as payment for storage at mainnet (see Billing requirements), reducing the dependency on token liquidity for tenants who only want to pay for storage without holding the native token.

---

## Emission Schedule

The network rewards allocation is released via the epoch reward mechanism over a multi-year emission schedule. The schedule is:

- **Declining emission**: Rewards per epoch decrease over time, creating scarcity and incentivizing early participation. The exact decay function (linear, halving, exponential) is a Tokenomics specification decision.
- **Minimum floor**: A non-zero minimum emission rate is maintained indefinitely to ensure node runners always have an incentive to participate, even when all tokens are "circulating." This floor is funded by network transaction fees (storage payments and egress fees flowing through the network) once the primary emission is exhausted.
- **Epoch emission is public**: The amount of tokens that will be emitted in any future epoch is deterministic and computable by anyone from the genesis configuration. There are no surprise increases in emission.

---

## Anti-Concentration Principles

- No single entity (team, investor, or foundation) may hold more than 20% of total supply at any point, accounting for unvested tokens.
- Large holder addresses are publicly known via on-chain disclosure (wallets associated with the team, foundation, and known investors are labeled).
- Governance votes require a minimum participation quorum to be valid — token concentration cannot win a governance vote with low turnout by design.
- The foundation commits to publishing a quarterly token transparency report showing circulating supply, vesting status, ecosystem grant disbursements, and foundation wallet balances.
