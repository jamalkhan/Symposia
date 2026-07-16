# Tokenomics — MVP Genesis Parameters

**Status:** MVP-authoritative  
**Audience:** Protocol engineering, node operators, foundation, investors  
**Last updated:** 2026-07-16  

This document **locks the economic parameters required for MVP launch** (Slice D in [MVP.md](../MVP.md)). Framework and principles in [Token Distribution and Launch Economics](./token-distribution-and-launch-economics.md) and [Blockchain and Tokenomics](./blockchain-and-tokenomics.md) still apply; **where numbers conflict, this file wins for mainnet genesis**.

All parameters are **governance-adjustable** after launch only via the thresholds in [Governance](./governance.md) (generally Tier 2–3 for economic changes). Genesis values are fixed at deploy.

**Provisional ticker:** `SYM` (final brand name may differ; contract symbol is a deploy-time constant).

---

## 1. Design goals

1. **Largest share of supply is earned** by running infrastructure (network rewards).  
2. **Predictable emission** — anyone can compute tokens minted in epoch *N* from genesis config.  
3. **Stake is real collateral** — enough to deter griefing; low enough to onboard operators at launch liquidity.  
4. **Dynamic type mix** — capacity types rebalance without manual ops; **new types** join via governance by re-slicing the same pie (see [extensible node types](../Platform/extensible-node-types-and-app-platform.md)).  
5. **Tenants can pay in stablecoins**; **operators are paid in SYM**.  
6. **Email IP Address nodes never receive mining rewards.**  
7. **Apps ≠ node types** — vendors extend the platform primarily as AppBuilders on existing infra; new mining types only when an open operator market is needed.  

---

## 2. Token basics

| Parameter | MVP value |
|---|---|
| Standard | ERC-20 + Permit (EIP-2612) + Votes (EIP-5805) on protocol L3 |
| **Total genesis supply (HARD CAP)** | **10,000,000,000 SYM** (10 billion) |
| Additional mint beyond this cap | **Impossible by design** — total supply is fixed at genesis |
| Emission schedule | Releases the pre-allocated **Network Rewards** slice into circulation over time; does **not** raise the 10B cap |
| Decimals | 18 |
| Canonical chain | Protocol L3 (OP Stack on Base); bridged copies on Base / ETH via canonical bridge |

### Hard cap (plain language)

**Yes — there is a hard cap of 10 billion SYM.**  

No contract path may mint the 10,000,000,001st token. Changing that would require a **Tier 3 governance** contract upgrade (supermajority), not an admin button.

What people sometimes confuse with “more tokens”:

| Happens | New tokens minted? |
|---|---|
| Epoch **emission** to node runners | No — those tokens were already counted inside the 10B (Network Rewards bucket unlocking) |
| Tenant pays fees in SYM | No — existing tokens move |
| Post-emission “floor” rewards | Paid from **fees already collected**, not by growing supply past 10B |
| Bridging to Base | No — same supply, wrapped representation |

### Why 10B

- Human-readable balances at sub-dollar unit prices.  
- Room for fine-grained per-byte and per-epoch accounting.  
- Aligns with “fixed cap + declining emission” narrative in distribution requirements.  

---

## 3. Genesis allocation (locked %)

Percentages promote the illustrative table in distribution requirements to **MVP law**:

| Category | % of supply | SYM (at 10B) | Release mechanism |
|---|---|---|---|
| **Network Rewards** | **40%** | 4,000,000,000 | Epoch emission only — never pre-airdropped to runners |
| **Ecosystem & Grants** | **15%** | 1,500,000,000 | Foundation/DAO over ≥5 years — includes **AppBuilder grants, usage match, hackathons** (see [appbuilder-incentives](../Platform/appbuilder-incentives.md)); **not** a second token |
| **Foundation Reserve** | **10%** | 1,000,000,000 | Vesting (below) |
| **Team / Early Contributors** | **15%** | 1,500,000,000 | Vesting (below) |
| **Investors** | **10%** | 1,000,000,000 | Vesting (below) |
| **Public Launch / Community** | **10%** | 1,000,000,000 | Sale / testnet rewards / airdrop (see §8) |
| **Total** | **100%** | 10,000,000,000 | |

### Vesting (on-chain, no admin acceleration)

| Category | Cliff | Total vest | Unlock |
|---|---|---|---|
| Team / Early Contributors | 12 months | 48 months | Linear monthly after cliff |
| Investors | 12 months | 36 months | Linear monthly after cliff |
| Foundation Reserve | 6 months | 36 months | Linear monthly after cliff |
| Ecosystem & Grants | None | ≥5 years deploy schedule | As awarded |
| Public Launch | Per mechanism | — | Unlocked at claim/sale |
| Network Rewards | N/A | ~8 year primary emission | Per epoch |

**Concentration rule:** no single entity (team, investor, or foundation wallet set) may control **>20%** of total supply including unvested balances — enforced by allocation design + disclosure, not a transfer hook.

---

## 4. Epochs

| Parameter | MVP value | Notes |
|---|---|---|
| **Epoch length** | **24 hours** | UTC-anchored; matches [node-runner-incentives](../Network/node-runner-incentives-and-penalties.md) |
| Epoch index | 0, 1, 2, … from genesis | On-chain key for rewards/slashes |
| Metric finality window | **15 minutes** after epoch end | Late reports accepted then sealed |
| Heartbeat / metric sample | Every **5 minutes** | Sub-epoch telemetry |
| Auto-payout heartbeat rule | **>90%** heartbeats in current **and** previous epoch | Else rewards → claimable reserve |
| Claimable reserve release | After **2** consecutive epochs back above 90% | No compounding while held |

**Resolved:** epoch length is **daily**, not hourly. Shorter epochs may be proposed later via governance if on-chain cost allows.

---

## 5. Network rewards emission

### 5.1 Source

Only the **Network Rewards** bucket (4B SYM) is minted into circulation via the epoch reward contract.

Ecosystem/foundation/team/investor/public allocations are **minted or pre-assigned at genesis into vesting/treasury contracts** — they are **not** taken as a haircut of each epoch’s runner payout.

### 5.2 Primary emission horizon

| Parameter | MVP value |
|---|---|
| Primary emission duration | **8 years** ≈ **2,922 epochs** (use **365.25 × 8** → contract uses exact epoch count **2,922**) |
| Shape | **Yearly step-down** (simple, auditable) |
| Post-primary | **Fee-funded floor** (see §5.4) |

### 5.3 Yearly share of the 4B Network Rewards pool

| Year after mainnet | % of Network Rewards pool | SYM that year | ≈ SYM / epoch (÷365) |
|---|---|---|---|
| 1 | 20% | 800,000,000 | ~2,191,781 |
| 2 | 16% | 640,000,000 | ~1,753,425 |
| 3 | 14% | 560,000,000 | ~1,534,247 |
| 4 | 12% | 480,000,000 | ~1,315,068 |
| 5 | 11% | 440,000,000 | ~1,205,479 |
| 6 | 10% | 400,000,000 | ~1,095,890 |
| 7 | 9% | 360,000,000 | ~986,301 |
| 8 | 8% | 320,000,000 | ~876,712 |
| **Total** | **100%** | **4,000,000,000** | |

Within a year, epoch emission is **uniform** across epochs in that year (deterministic).

Contract stores `emissionPerEpoch[yearIndex]`; yearIndex = `min(7, floor(epochIndex / 365))` with year 8 covering remaining epochs through 2921, then floor mode.

### 5.4 Post-primary floor (after epoch 2922+)

```
epoch_emission = max(
  protocol_fee_share_to_runners,   // §7
  MIN_EPOCH_EMISSION
)
```

| Parameter | MVP value |
|---|---|
| `MIN_EPOCH_EMISSION` | **50,000 SYM / epoch** | Paid only if fee share is lower; funded first from fee treasury, then may pause floor if treasury empty (governance alert) |
| Prefer | Fee share when usage is healthy | Avoid infinite inflation beyond cap — **floor must not mint above remaining unminted Network Rewards** once that bucket is exhausted |

Once the 4B Network Rewards bucket is fully emitted, **no new SYM is minted**. Runner incentives continue from:

1. Share of protocol fees (§7), and  
2. Optional ecosystem grants (not automatic).

### 5.5 Who receives epoch emission

| Recipient class | Share of that epoch’s emission | Notes |
|---|---|---|
| **Capacity nodes** (Storage, OLTP, Analytics, Consensus, **Serverless**) | **92%** | Split by dynamic type pools (§6) then performance scores |
| **Verifier nodes** | **8%** | Separate score (attestation quality, uptime); see [verifier-nodes](../Network/verifier-nodes.md) |
| **Email IP Address nodes** | **0%** | Hardcoded exclusion |

---

## 6. Dynamic type pools & performance scoring

### 6.0 Dynamic multiplier — plain-language explanation

**Dynamic mult does not print extra tokens.** Each day the network still only has a **fixed pie** of new rewards for that epoch (from the emission schedule).

The pie is first split into **type buckets** (Storage, OLTP, Analytics, Consensus, Serverless).  
**Base weights** say the “normal” split (e.g. Storage often gets the largest share).

**Dynamic multiplier** then nudges those buckets based on **how busy each type is relative to how many nodes offer it**:

- Lots of work, few nodes of that type → **raise** that type’s share (multiplier up).  
- Many nodes idle, little work → **lower** that type’s share (multiplier down).  
- Multipliers are **clamped** (MVP: not below 0.5×, not above 3.0×) so one type cannot take everything.  
- After nudging, shares are **renormalized** so they still add up to **100% of the same pie** — if Serverless gets more, someone else gets slightly less **that epoch**.

**Toy example:**  
Epoch pie for capacity nodes = 1,000,000 SYM.  
Base: Storage 40%, Serverless 12%, …  
If Serverless is scarce and slammed, its mult might be 2.0× and Storage 0.8×; after renormalize, Serverless might land ~20% of the pie and Storage ~35% — **still only 1,000,000 SYM total**, just different slices.

That is the whole idea: **steer operators toward under-served node types** without changing the hard supply cap.

**Adding a brand-new type later** (CDN, GPU, vendor-driven specialty): governance assigns a **base weight**, **rescales other types so weights still sum to 100%**, and the same dynamic mult applies. Emission schedule and **10B hard cap are unchanged**. See [Extensible Node Types & Application Platform](../Platform/extensible-node-types-and-app-platform.md).

### 6.1 Base pool weights (before demand adjustment)

Of the **92% capacity** emission:

| Node type | Base weight | Rationale |
|---|---|---|
| **Storage** | **40%** | Core durability product |
| **OLTP** | **22%** | Martech + DB compute |
| **Analytics** | **13%** | Report/query tier |
| **Consensus** | **13%** | Chain security / participation |
| **Serverless** | **12%** | Event-driven functions, webhooks, Journey side-effects |

Weights sum to 100% of the capacity slice.

### 6.2 Dynamic multiplier (formula)

Each epoch, for each capacity type:

```
raw_mult(type) = clamp(
  1.0 × (demand_utilization(type) + ε) / (network_supply_share(type) + ε),
  MULT_MIN,
  MULT_MAX
)
```

| Parameter | MVP value |
|---|---|
| `MULT_MIN` | **0.5** |
| `MULT_MAX` | **3.0** |
| `ε` | **0.02** | Avoid divide-by-zero / zero-demand collapse |

Then **renormalize** so adjusted weights still sum to 100% of the capacity emission:

```
adjusted_weight(type) = base_weight(type) × raw_mult(type)
type_pool(type) = capacity_emission × adjusted_weight(type) / sum(adjusted_weight)
```

**Definitions (MVP):**

- `network_supply_share(type)` = active eligible nodes of type / all active eligible capacity nodes (not weighted by stake in v1).  
- `demand_utilization(type)` = clamped 0–1 measure of used vs committed capacity:
  - Storage: bytes used / bytes offered  
  - OLTP: billed compute-seconds / committed  
  - Analytics: query-seconds / committed  
  - Consensus: participation rate  
  - **Serverless:** delivered invoke GB-seconds (or concurrent slot utilization) / committed slots  

Exact telemetry schemas live in node requirements; economics only require a **deterministic, published formula** from attested metrics.

### 6.3 Within-type performance score (Storage example)

Normalized 0–1 per factor vs best node that epoch; weighted sum:

| Factor | Weight |
|---|---|
| Retrieval speed | **0.30** |
| Uptime | **0.20** |
| Latency (TTFB) | **0.15** |
| I/O throughput | **0.10** |
| Network bandwidth | **0.10** |
| Available storage | **0.08** |
| Used storage | **0.07** |
| **Total** | **1.00** |

**OLTP / Analytics / Consensus** use type-specific factor tables (publish in node docs); MVP requirement is the **same scoring pipeline** with different weights:

| Type | Primary factors (illustrative weights — finalize in node ops runbook) |
|---|---|
| OLTP | Uptime 0.25, query latency 0.25, commit success 0.20, CPU reliability 0.15, connection capacity 0.15 |
| Analytics | Uptime 0.20, query latency 0.25, bytes scanned efficiency 0.20, memory reliability 0.20, availability 0.15 |
| Consensus | Uptime 0.35, participation 0.30, propagation latency 0.20, correctness 0.15 |
| Serverless | Uptime (accept) 0.20, invoke success rate 0.25, p95 invoke latency 0.20, sandbox isolation health 0.15, throughput (invokes) 0.20 |

### 6.4 Reliability multiplier

```
payout = type_pool_share × (node_score / sum_scores_in_type) × reliability_score × stage_penalty_mult
```

| Parameter | MVP value |
|---|---|
| `reliability_score` | Starts at 1.0; reduced by failed challenges (storage PoR, resource probes) |
| Reliability bonus | If trailing 30-epoch reliability ≥ **0.98**, multiply by **1.05** (capped so type pool still balances — bonus funded by slight reduction of non-bonus peers **or** from fee treasury; **MVP: peer-normalized within type after bonus**) |
| Stage penalty mult | Stage 1 → 0.70; Stage 2 → 0.40; Stage 3 → 0; Stage 4 → 0 (see incentives doc) |

### 6.5 Time-limited bootstrap bonuses

| Parameter | MVP value |
|---|---|
| Max bonus mult on a type | **+50%** on type pool for that type |
| Max duration | **30 epochs** |
| Funding | Ecosystem treasury or Foundation (not silent inflation) |
| Approval | Governance Tier 2 **or** bootstrap multi-sig during first 180 days only |

---

## 7. Protocol fees → operators & floor

Tenant spend (storage, egress, compute) is denominated in **SYM credits**. At epoch settlement:

| Destination | Share of protocol fee revenue (SYM) | Notes |
|---|---|---|
| **Capacity operators** | **70%** | Pro-rata by same type pools + scores as emission (or storage-weighted for pure storage fees — **MVP: same score pipeline**) |
| **Platform / foundation** | **25%** | Ops, gateway, R&D |
| **Fee treasury (floor buffer)** | **5%** | Funds `MIN_EPOCH_EMISSION` and shortfalls |

**Compute-specific (OLTP):** of the **operator 70%** attributable to compute billing, **100%** goes to OLTP node operators that served that work (metered). The 70/30 split vs platform is the global fee split above (70% operators total, not an extra cut).

**Clarification for [database-billing](../Database/database-billing.md):** MVP operator compensation = **70% of compute fee revenue to OLTP operators**, **30% platform+treasury** (25%+5%).

---

## 8. Public launch & liquidity (MVP)

| Mechanism | MVP choice |
|---|---|
| Primary | **DEX liquidity on Base** + **testnet contributor allocation** |
| Public sale | Optional; if used, per-wallet cap and published price |
| Airdrop | Optional merkle claim; **6 month** claim window; unclaimed → Ecosystem |
| Testnet rewards | From Public Launch bucket; formula published before testnet end |
| Initial DEX seed | From Public Launch and/or Foundation per legal advice; target **≥ $500k** TWAP pool depth when possible (oracle requirement) |

Node runners must be able to **acquire SYM to stake** at or before mainnet open (public launch concurrency requirement from distribution doc).

---

## 9. Staking minimums (MVP)

Stake is in **SYM**, locked for the node’s registration. Unstake subject to **cooldown**.

| Parameter | MVP value |
|---|---|
| Unstake cooldown | **21 days** |
| Partial unstake | Allowed if remaining stake ≥ minimum for declared capacity |
| Mid-epoch drop below minimum | **Forfeit rewards for that epoch**; grace **1 epoch** before forced deregister |

### 9.1 By node type

| Node type | Minimum stake | Scaling |
|---|---|---|
| **Storage** | **25,000 SYM** base | **+ 2,500 SYM per TB** offered (ceil) |
| **OLTP** | **75,000 SYM** base | **+ 15,000 SYM per compute-size step** above `medium` (see database sizes: large=+1, xlarge=+2, …) |
| **Analytics** | **60,000 SYM** base | **+ 10,000 SYM per 32 GB RAM** committed above 32 GB |
| **Consensus** | **150,000 SYM** | Flat per consensus node identity |
| **Serverless** | **50,000 SYM** base | **+ 5,000 SYM per concurrent invoke slot** committed (ceil) |
| **Verifier** | **5×** the Storage base for a **0 TB** “verifier-only” registration = **125,000 SYM** | Matches “5× standard” intent in verifier requirements |
| **Email IP Address** | **0 SYM mining stake** | Optional **abuse bond** later; not mining collateral |

### 9.2 Stake-to-earn communication

Publish before mainnet a **calculator** using:

- Genesis epoch emission  
- Assumed N nodes of each type  
- Baseline reliability 1.0  

**Target design band:** well-behaved Storage node recovers stake principal from rewards in roughly **6–18 months** at early-network node counts — **not a guarantee**; labeled estimate only.

### 9.3 Cross-type co-location (isolation)

| Rule | MVP |
|---|---|
| Multiple rewarded types on one machine | **Allowed** |
| Dedicated hardware enforced by protocol? | **No** |
| Enforcement | Each container’s **resource guarantees are binding**; failures reduce reliability score / stages independently |
| Guidance | Recommend isolating Analytics from OLTP above OLTP `large` or Analytics >64 GB RAM |
| Sybil | Same hardware must not register **duplicate identities** for the same capacity (existing anti-Sybil rules) |

---

## 10. Payment & unit of account

| Parameter | MVP |
|---|---|
| Credit denomination | **SYM** |
| Stablecoins | USDC (primary), USDT, DAI on Base per payment spec |
| Swap | Stablecoin → SYM at credit purchase; 1% max slippage |
| TWAP window | **1 hour wall-clock** (not “6×10m epochs” — that wording is obsolete given 24h reward epochs) |
| Non-payment | Grace → soft suspend → soft delete → hard delete per billing spec |

---

## 11. What is *not* paid by mining

| Participant | Mining rewards |
|---|---|
| Storage / OLTP / Analytics / Consensus / **Serverless** | Yes |
| Verifiers | Yes (8% slice) |
| Email IP Address nodes | **No** |
| Marketers / tenants | No (they pay) |
| Gateways / platform proxies | No (funded by platform fee share) |

---

## 12. Genesis checklist (engineering)

- [ ] ERC-20 + vestings + emission contract with year table  
- [ ] Epoch controller (24h, seal, finality window)  
- [ ] Stake registry per node type + capacity  
- [ ] Reward distributor: type pools → scores → reliability → stages  
- [ ] Verifier 8% path  
- [ ] Hard zero for Email IP node type ID  
- [ ] Fee splitter 70/25/5  
- [ ] Public parameter viewer (API + docs site)  
- [ ] Testnet: ≥3 capacity nodes + ≥1 verifier complete 2 full epochs with non-zero payouts  

---

## 13. Open items (non-blocking vs blocking)

| Item | Blocking for mainnet? | Notes |
|---|---|---|
| Final public brand ticker (if not SYM) | No | Cosmetic |
| Exact testnet → mainnet reward conversion formula | Yes before public sale | Publish with testnet docs |
| Legal opinion on token classification | Yes for public distribution | Counsel |
| CEX listing | No | Post-MVP |
| Halving-style emission alternative | No | Would be governance change |

---

## 14. Resolved cross-doc open questions

| Source | Resolution |
|---|---|
| node-types-and-rewards Q1 epoch length | **24 hours** |
| node-types-and-rewards Q2 emission | **Fixed declining schedule** from Network Rewards bucket; post-cap **fee floor**, no uncapped mint |
| node-types-and-rewards Q3 stake minimums | **§9 tables** |
| node-types-and-rewards Q4 co-location | **Allowed**; reliability-enforced, not hardware-mandated |
| database-billing compute split | **70% operators / 30% platform+treasury** via fee split |
| token-distribution “exact figure TBD” | **10B total; §3–5 numbers** |

---

## 15. References

- [MVP.md](../MVP.md) — Slice D  
- [token-distribution-and-launch-economics.md](./token-distribution-and-launch-economics.md)  
- [blockchain-and-tokenomics.md](./blockchain-and-tokenomics.md)  
- [chain-architecture.md](./chain-architecture.md)  
- [governance.md](./governance.md)  
- [node-types-and-rewards.md](../Platform/node-types-and-rewards.md)  
- [node-runner-incentives-and-penalties.md](../Network/node-runner-incentives-and-penalties.md)  
- [payment-and-stablecoin-integration.md](../Platform/payment-and-stablecoin-integration.md)  
- [retention-and-billing.md](../Platform/retention-and-billing.md)  
- [network-bootstrapping-and-cold-start.md](../Network/network-bootstrapping-and-cold-start.md)  
