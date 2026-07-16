# AppBuilder / Vendor Incentives

## Overview

Node operators are incentivized by **mining** (stake + epoch rewards). **Vendors (AppBuilders)** are different: they ship software and data products on the platform. They should be incentivized to **build, ship, and attract usage** — not to run random hardware for emissions.

This document answers:

1. Should vendors have a **separate protocol token**?  
2. If not, **how do we incentivize them** under the existing **10B SYM** hard cap?

Related: [Extensible Node Types & Application Platform](./extensible-node-types-and-app-platform.md), [Tokenomics MVP](../Blockchain/tokenomics-mvp.md), [Stakeholders — AppBuilder](./stakeholders-and-personas.md#3-appbuilder).

---

## Recommendation (MVP / mainnet)

### Do **not** create a second *protocol* token for vendors

| Issue with a second network token (“BUILD”, “APP”, …) | Why it hurts |
|---|---|
| **Split liquidity** | Marketers pay SYM/stablecoins; operators earn SYM; a second token fights for attention and DEX depth |
| **Confused utility** | What is BUILD for — votes? fees? mining? Usually unclear and gameable |
| **Double governance** | Two token-holder politics vs one protocol |
| **Regulatory surface** | Two instruments to classify and distribute |
| **Hard cap story breaks** | Either BUILD is inflationary or it’s a rebranded slice of SYM with extra complexity |

**Protocol money stays one unit: SYM** (plus stablecoins for tenant payment UX).

### What vendors *may* do

| Allowed | Notes |
|---|---|
| **Charge marketers** (SaaS, usage, revenue share) | Primary long-term incentive — real business |
| **Issue their own app/community token** | Off-protocol or as a separate project; Symposia does **not** mint it, list it as gas, or pay mining in it |
| **Earn SYM** from **Ecosystem / Builder programs** | Grants, bounties, usage matching — from the **existing** Ecosystem allocation |

---

## How we incentivize builders (the model)

Think of three layers. Mining is **not** layer 1 for vendors.

```
1. COMMERCIAL  →  Marketers pay the vendor for the app (primary)
2. PROTOCOL    →  Vendor (or marketer) pays SYM credits for infra usage
3. ECOSYSTEM   →  Foundation/DAO pays SYM grants / match / bounties to bootstrap supply of apps
```

Operators are paid from **Network Rewards + fee share**.  
Builders are paid from **customers + Ecosystem & Grants (and optional fee kickbacks)**.

---

## Funding sources (no new mint)

All builder incentives come from tokens **already inside the 10B cap**:

| Source | Size (MVP) | Role for vendors |
|---|---|---|
| **Ecosystem & Grants** | **15%** = 1.5B SYM | Primary builder treasury (see sub-buckets below) |
| **Protocol fee share** | 25% platform + optional carve | Optional **Builder Match** % of *attributable* app-driven fees (governance) |
| **Public / community** | 10% | Occasional hackathon/airdrop slices, not ongoing mining |
| **Network Rewards 40%** | 4B | **Not** for AppBuilders — operators only |

### Ecosystem sub-buckets (MVP target)

Of the **1.5B Ecosystem** allocation, foundation/DAO **targets** (not hard-coded immutably, but policy for first 5 years):

| Sub-bucket | Share of Ecosystem | SYM | Purpose |
|---|---|---|---|
| **Builder grants & RFPs** | 40% | 600M | Milestone grants for apps that extend the platform |
| **Usage match / growth** | 25% | 375M | Match marketer spend on qualifying apps (cold-start demand) |
| **Hackathons / bounties / education** | 15% | 225M | Ship small, prove integrations |
| **Integrations & open source** | 10% | 150M | Connectors, SDKs, reference apps |
| **Strategic / partnerships** | 10% | 150M | Large vertical partners |

Disbursement is **vested / milestone-based** (not one free unlock on “hello world”). Unspent remains in ecosystem treasury.

---

## Incentive instruments (what vendors actually get)

### 1. Milestone grants (core)

| Stage | Example milestone | Payout style |
|---|---|---|
| Design | Spec + security review | Small SYM grant |
| Alpha | Runs on testnet / sandbox tenant | Tranche 2 |
| Production | N paying marketers or M invocations | Tranche 3 |
| Scale | Quality + compliance audit | Final tranche + listing badge |

Paid in **SYM** (optional partial stablecoin from foundation ops for tax/UX). **Clawback** if milestones faked.

### 2. Usage match (growth)

For a limited window after an app is listed:

```
match = min(
  match_rate × platform_credits_spent_through_app,
  per_app_epoch_cap,
  program_budget_remaining
)
```

| Parameter | MVP starting policy (governance-tunable) |
|---|---|
| `match_rate` | **20%** of attributable platform usage credits driven by the app |
| Cap per app per 30 days | e.g. **50,000 SYM** |
| Duration | First **90 days** after listing, or until sub-bucket spent |
| Paid to | AppBuilder wallet (not marketers) |

This **does not increase total supply**; it spends Ecosystem tokens to subsidize early traction.

### 3. Fee kickback (optional, post-MVP-ok)

When marketplace metering can attribute fee revenue to an app:

| Split of **platform’s 25% fee share** attributable to that app | Example |
|---|---|
| Platform keeps | 60% of that 25% |
| Builder kickback | 40% of that 25% |

I.e. builder gets **10% of gross protocol fees** generated by their app’s usage (0.4 × 0.25).  
**Operator 70% is untouched** — builders never eat miner rewards.

MVP may ship grants + match first; kickback when marketplace attribution is solid.

### 4. Demand-side boosts (help them sell)

- **Template gallery / App directory** featuring certified apps  
- **Co-marketing** with foundation  
- **Default routing** in Journey actions (“Send via Vendor X webhook”) when marketer opts in  
- **Data Cloud / enrichment** catalog listing (when that product ships)  

### 5. Not incentives (disallowed as “mining for apps”)

| Anti-pattern | Why blocked |
|---|---|
| App claims to be a “node type” to farm epoch emission | Mining is for attested infrastructure only |
| Infinite SYM mint for installs | Breaks hard cap / trust |
| Paying builders from Network Rewards pie | Dilutes operators; wrong job |

---

## “Can vendors have a token?” — decision matrix

| Idea | Protocol support? | Recommendation |
|---|---|---|
| Second **network** token for all builders | No for MVP/mainnet design | **Reject** as protocol instrument |
| Vendor’s **own** token for their app community | Yes (their business) | Allowed; no SYM mint; no mining rights |
| **Points** that convert to SYM from Ecosystem later | Optional | OK if conversion pool is pre-funded Ecosystem |
| **Revenue share in SYM** via marketplace | Yes | Preferred long-term commercial path |
| **Builder mining** equal to Storage | No | Category error |

---

## Alignment with AppBuilder data rules

Incentives never override:

- Individual ownership of identity-layer data  
- Permission gates (`data_read`, `data_enrichment`, …)  
- Erasure / anonymization obligations  
- Namespace separation of enrichment  

Grant agreements require compliance with AUP and privacy architecture. Fraudulent “usage” for match rewards is slashable from remaining grant vesting and can delist the app.

---

## Simple mental model

| Role | “Wage” |
|---|---|
| **Node operator** | SYM mining + fee share for capacity |
| **AppBuilder** | Customer revenue + Ecosystem grants/match (+ later fee kickback) |
| **Marketer** | Pays for outcomes; gets software + infra |

You incentivize vendors the way successful platforms do: **make it profitable to have customers on Symposia**, and **seed the cold start with grants/match in the same token everyone already uses (SYM)** — not a second mining coin.

---

## Implementation checklist

- [ ] Ecosystem treasury wallets + vesting for builder sub-buckets  
- [ ] App registry (listing, publisher key, compliance attestations)  
- [ ] Grant milestone contract or off-chain process with on-chain payout  
- [ ] Usage attribution tags (`app_id` on invokes / API calls) for match/kickback  
- [ ] Public dashboard: grant recipients, amounts, milestones (transparency)  
- [ ] Docs for “Build on Symposia” (APIs, Serverless, billing)  

---

## Open parameters (governance, not philosophy)

Exact match rates, caps, and kickback % may change via Tier 1–2 governance. The **invariants** are:

1. **One protocol token (SYM)** for network value.  
2. **No builder claim on Network Rewards mining pie.**  
3. **Builder incentives funded from Ecosystem (and optional platform fee share).**  
4. **Vendors may issue their own tokens; protocol does not.**  

---

## References

- [Tokenomics MVP](../Blockchain/tokenomics-mvp.md) — 15% Ecosystem, hard cap  
- [Extensible node types](./extensible-node-types-and-app-platform.md)  
- [Token distribution](../Blockchain/token-distribution-and-launch-economics.md)  
- [Governance](../Blockchain/governance.md)  
