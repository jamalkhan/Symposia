# Extensible Node Types & Application Platform

## Overview

Symposia is not only a fixed set of infrastructure roles. It is an **application development platform**: marketers use first-party products (email, journeys, tracking), and **vendors (AppBuilders)** build apps that extend the network — models, connectors, vertical workflows, specialized compute.

This document explains how **growth of functionality** maps to:

1. **Software apps** (no new mining type), and  
2. **New node types** (new economic/resource classes for operators),

and how **tokenomics accounts for new types** without breaking the hard supply cap or rewriting the reward system each time.

Related: [Stakeholders](./stakeholders-and-personas.md), [Node Types and Rewards](./node-types-and-rewards.md), [Tokenomics MVP](../Blockchain/tokenomics-mvp.md), [Governance](../Blockchain/governance.md).

---

## Two layers of extension (do not confuse them)

```
┌─────────────────────────────────────────────────────────────┐
│  APPS (AppBuilders / vendors)                               │
│  Products, models, connectors, UIs, APIs sold to marketers  │
│  Run on existing (or new) infrastructure primitives         │
└───────────────────────────┬─────────────────────────────────┘
                            │ invoke / store / query
┌───────────────────────────▼─────────────────────────────────┐
│  INFRASTRUCTURE NODE TYPES (operators / miners)             │
│  Storage · OLTP · Analytics · Consensus · Serverless · …    │
│  Stake · epoch rewards · dynamic type pools · fees          │
└─────────────────────────────────────────────────────────────┘
```

| Layer | Who | What they “ship” | Earn mining rewards? |
|---|---|---|---|
| **App** | AppBuilder / vendor | Software + data products (e.g. propensity scores, Shopify sync, vertical Journey packs) | **No** — they earn **commercial revenue** (licenses, usage fees from marketers) |
| **Node type** | Node operators | Hardware + agent software matching a **resource profile** | **Yes** (if the type is a rewarded capacity type) |

**Most vendor expansion never needs a new node type.** A fraud-scoring vendor, a “best send time” model, or a CRM connector should default to:

- **Serverless** (invoke),  
- **OLTP** (state),  
- **Storage** (artifacts / models),  
- **Analytics** (batch features),  
- existing **NATS** subjects / APIs.

A **new node type** is only justified when the network needs a **distinct operator market** with its own hardware profile, metering, and incentive weight (e.g. GPU inference fleet, global CDN edge, specialized messaging brokers).

---

## How apps use the network (default path)

1. Vendor registers as an **AppBuilder** (platform account + policies).  
2. App is installed by a marketer (tenant); permissions and data ownership follow [User Data Ownership](../Identity/user-data-ownership.md) and enrichment rules.  
3. Runtime:
   - Hot path logic → **Serverless** invocations  
   - Durable tenant data → marketer **Postgres (OLTP)** or app-scoped tables under policy  
   - Large artifacts → **blob Storage**  
   - Heavy batch → **Analytics** jobs  
4. Marketer (or app) pays **platform credits (SYM)** for underlying usage; protocol fee split pays **node operators** (§ fee rules in tokenomics).  
5. Vendor bills the marketer separately for the **app** (off-protocol or via platform commerce later).

**No change to emission schedule. No new mining category.** Demand for Serverless/OLTP/Storage simply rises → **dynamic multipliers** already push more of the **fixed daily reward pie** toward those types (see below).

---

## When to introduce a new node type

Consider a new rewarded type only if **most** of these are true:

| Criterion | Why |
|---|---|
| Workload needs **specialized hardware** not fairly shared with existing types (GPU, edge POP, huge RAM) | Generic Serverless/OLTP can’t price or schedule it honestly |
| Operators need a **public market** to supply capacity (not just vendor-run servers) | Decentralized supply is a product goal |
| **Metering** is distinct (e.g. GPU-seconds, edge GB egress) | Demand signal for dynamic mult must be well-defined |
| Failure modes / SLA differ | Reliability scoring must be type-specific |
| Security isolation requires a dedicated agent profile | Sandbox/attestation differs |

If the vendor can run the specialty hardware **themselves** and only expose an API, that can remain an **app** (vendor-hosted) until the network wants open operator participation.

---

## How tokenomics accounts for a new node type

### Core invariant

**Hard cap and epoch emission stay fixed.**  
New types **do not mint extra tokens**. They only get a **share of the existing capacity reward pie** (the 92% capacity slice of each epoch’s Network Rewards emission, after verifiers’ 8%).

Think: new kid at the table → **re-slice the same pizza**, don’t bake a second pizza.

### Registration package (required to add a type)

A Tier **2 or 3** governance proposal (economic parameters → typically Tier 2; if it changes emission caps or core reward math → Tier 3) must publish:

| Field | Description |
|---|---|
| `type_id` | Stable string / enum (e.g. `gpu_inference_v1`) |
| Resource profile | Min CPU/RAM/GPU/disk/network; container image requirements |
| Stake formula | Base + scaling (same style as [Tokenomics MVP §9](../Blockchain/tokenomics-mvp.md#9-staking-minimums-mvp)) |
| **Base pool weight** | Initial % of capacity emission (must specify how other weights adjust) |
| Demand signal | On-chain or attested metric: utilization 0–1 each epoch |
| Supply signal | Count (or capacity-weighted count) of active eligible nodes |
| Score factors | Weights for within-type performance (like Storage’s retrieval/uptime table) |
| Penalty mapping | Which failures map to stages 1–4 |
| Bootstrap bonus | Optional time-limited mult (max +50%, ≤30 epochs) funded from ecosystem/foundation |
| Fee metering | How tenant usage of this type becomes protocol fees |

### Base weight insertion (renormalization)

At activation epoch \(T\):

1. New type enters with approved `base_weight_new` (e.g. 8%).  
2. **All prior capacity base weights are scaled by** `(1 - base_weight_new)` so the set still sums to 100%.  

Example: before Serverless existed, Storage was 45%. After adding Serverless at 12% (as in current MVP tables), remaining types were rescaled. Same procedure for `gpu_inference` at 8%:

```
weight_i' = weight_i × (1 - 0.08)   for each existing type
weight_gpu = 0.08
```

Governance may instead pass an **explicit full weight table** (preferred for clarity). Dynamic mult continues to apply on the new set automatically.

### Dynamic multiplier still works

Once the type has:

- `network_supply_share(type)`, and  
- `demand_utilization(type)`,  

the **same** formula applies — no special case:

```
raw_mult = clamp(demand / supply, 0.5, 3.0)
```

then renormalize across **all** capacity types including the new one.

So: if a vendor ecosystem suddenly needs GPU nodes and few exist, GPU mult rises and operators chase rewards — **without** a foundation manually reallocating emission forever.

### Bootstrap period

New types often start with **near-zero supply**. Dynamic mult alone may not bootstrap fast enough.

| Tool | Use |
|---|---|
| **Bootstrap bonus** | Temporary +% on that type’s pool (ecosystem-funded) |
| **Foundation nodes** | Seed capacity under same stake/reward rules |
| **App-side minimum spend** | Platform products route a floor of paid load to the new type |

After bootstrap ends, only organic demand + mult remain.

### What does *not* change when types are added

| Unchanged | Why |
|---|---|
| **10B hard cap** | Types don’t mint |
| **Yearly emission table** | Still the same epoch pie size |
| **Verifier 8% / capacity 92%** (unless governance retunes) | Structural split |
| **Email IP = 0% mining** | Permanent product rule unless governance changes the type registry |
| **Credit denomination in SYM** | Apps still settle usage through platform metering |

---

## Lifecycle of a type

```
Proposal → Spec + stake/weight/score → Governance vote →
On-chain type registry entry → Operators install agent →
Bootstrap bonus (optional) → Steady state (dynamic mult) →
(Optional) Deprecate: weight → 0 over N epochs, unstake after cooldown
```

**Deprecation:** set base weight to 0 (or phase down), stop new registrations, allow existing nodes to drain stake after cooldown. Emission automatically flows to remaining types via renormalization.

---

## Application platform: vendor economic model

```
Marketer pays vendor (app fee)          ← primary builder incentive
        │
        ▼
Vendor app runs (Serverless / OLTP / …)
        │
        ▼
Marketer (or app on marketer’s behalf) pays platform usage credits
        │
        ▼
Protocol fee split → node operators (70%) + platform (25%) + treasury (5%)
        │
        └── optional Builder Match / fee kickback from Ecosystem or platform share
```

**Builders are not miners.** How we fund grants, usage match, and marketplace kickbacks in **SYM** (and why there is **no second protocol token** for vendors): [AppBuilder / Vendor Incentives](./appbuilder-incentives.md).

Optional future: **on-protocol app marketplace** with escrowed SYM and revenue share — still does **not** require each app to be a node type.

### AppBuilder responsibilities

- Respect data ownership, consent, enrichment namespaces  
- Declare which infrastructure types the app will consume (for capacity planning)  
- Prefer existing types; propose a new type only with operator-market justification  
- Cannot self-mint rewards by inventing a fake node type without governance  

### Platform responsibilities

- Publish type registry and parameters  
- Enforce sandboxing for Serverless and any multi-tenant execute path  
- Expose metering so apps and operators can predict cost and yield  
- Keep dynamic pool math public and deterministic  

---

## Worked examples

### Example A — Vendor ships “Send-time AI” without a new type

- Model inference on **Serverless** (or batch on **Analytics**).  
- Scores written to `contact_enrichment` with `owner_type = appbuilder`.  
- Demand for Serverless rises → Serverless dynamic mult up → more operators run Serverless.  
- **No governance proposal for a new type.**

### Example B — Network adds GPU Inference type

- Apps need long-running GPU jobs that Serverless timeouts can’t host.  
- Governance adds `gpu_inference` at 8% base weight; others rescale.  
- 30-epoch bootstrap +50% bonus.  
- Operators stake per GPU formula; earn from capacity pie + fees when marketers run GPU jobs.  

### Example C — Vendor-only proprietary box

- Vendor runs private GPU cloud, exposes HTTPS API only.  
- Remains an **app** with vendor-hosted infra.  
- Symposia miners don’t earn from that GPU; vendor isn’t forced onto the open network until they (or governance) want open supply.  

---

## MVP vs later

| MVP | Later |
|---|---|
| Fixed genesis types: Storage, OLTP, Analytics, Consensus, Serverless + Verifiers | CDN, ML/GPU, Messaging Relay, vendor-proposed types |
| Governance path documented (this doc) | Type registry UI, automated weight proposals |
| Apps use Serverless/APIs | Full marketplace, on-protocol app billing |

---

## Summary

| Question | Answer |
|---|---|
| How do we account for new node types economically? | Give them a **base weight** in the **same fixed emission pie**; **dynamic mult** allocates day-to-day; **no new minting**. |
| How do vendors extend the platform? | Prefer **apps on existing types**; promote a **new node type** only for open, specialized operator markets. |
| Does every app need a node type? | **No.** |
| Does adding types break the hard cap? | **No.** |

---

## References

- [Node Types and Dynamic Rewards](./node-types-and-rewards.md)  
- [Tokenomics MVP §6](../Blockchain/tokenomics-mvp.md#6-dynamic-type-pools--performance-scoring)  
- [Governance](../Blockchain/governance.md)  
- [Stakeholders — AppBuilder](./stakeholders-and-personas.md#3-appbuilder)  
- [Network bootstrapping](../Network/network-bootstrapping-and-cold-start.md)  
