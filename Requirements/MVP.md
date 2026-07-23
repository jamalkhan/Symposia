# Symposia MVP — One-Pager

**Status:** Product scope definition  
**Audience:** Engineering, product, legal, tokenomics  
**Last updated:** 2026-07-15  

This document defines **what ships in MVP** and what is explicitly **out of scope**. Detailed behavior lives in the linked requirements; this page is the cut line.

---

## One-sentence MVP

**A marketer can onboard, import contacts, track a site, send authenticated marketing email from their own IP path (or shared-pool onramp), run a basic Campaign and Journey automation, and honor unsubscribe / delete — while the decentralized network is live with full mining tokenomics (stake, epoch rewards, penalties/slashing, and tenant payment for resources) and individuals can exercise marketer-scoped rights without a wallet.**

---

## Goals

| Goal | Success looks like |
|---|---|
| **Send** | Marketer sends a Broadcast (newsletter) and a simple Triggered email with DKIM/SPF, unsub, suppression |
| **Automate** | At least one multi-step Journey (cart abandon **or** welcome series) runs end-to-end |
| **Collect** | JS tracker + pixel; brand cookie; consent events persisted |
| **Audience** | CSV/API contact create, lists, segments for targeting |
| **Rights** | One-click unsub, preference center, T1 delete/access for a single marketer |
| **Trust path** | Compliance events on NATS; hourly Merkle commitments to chain |
| **Network economics** | **Full mining tokenomics live**: emission schedule, epoch scoring, rewards paid to eligible node types, staking + progressive penalties, tenant prepaid credits for storage/compute/egress settled in native token (and stablecoin path if specified) |

---

## Non-goals (explicit)

| Out of MVP | Notes |
|---|---|
| Hosted e-commerce storefront | Phase 2 stub |
| Symposia Data Cloud licensing / cross-brand enrichment product | Schema may exist; no paid tier / marketer Data Cloud API |
| Ad platforms, CRM, ESP, warehouse, CDP | Webhooks-only if anything |
| SMS / push | Email only |
| Ad-hoc SQL analytics / segment explorer | Report endpoints only if analytics ships at all |
| Multi-touch attribution | Last-touch optional later |
| Website scraper / full ETL marketplace | Manual API + CSV + optional single feed path max |
| Network-wide frequency caps across brands | Per-tenant only + global opt-out if T2 ready |
| Full malicious-sender AI suite | **Minimum** gates required (below); full system post-MVP |
| L3 → standalone L1 migration | Architecture reserved; not an MVP event |
| Decentralized sequencer | Centralized sequencer at launch is OK per chain architecture |
| Email IP Address nodes earning rewards | By design they **never** mine — relay only |

**Not a non-goal:** mining tokenomics, epoch rewards, staking/slashing, and economic participation of Storage / OLTP / Analytics / Consensus nodes. Those are **MVP requirements**.

---

## Primary personas in MVP

| Persona | MVP experience |
|---|---|
| **Marketer** | Tenant account, sender profiles, sending domain, email path (shared pool **or** Email IP node), contacts, campaigns, journeys; pays for platform usage via credits |
| **Individual (T0/T1)** | Cookie consent, unsub, preference center, OTP delete/view for **one** marketer |
| **Individual (T2+)** | Nice-to-have if ready; **not** required to launch marketer send |
| **Node runner** | Onboard, stake, run ≥1 rewarded node type, pass region/verification as required, earn epoch rewards, subject to penalties |
| **Email IP operator** | Marketer (or host) runs Email IP node when off shared pool — **no mining yield** |
| **Verifier** | Enough verifier capacity for region/performance checks (foundation verifiers OK at cold start per network bootstrap) |

---

## End-to-end slices (must work)

### Slice A — Broadcast send

1. Tenant onboards → verify **sending domain** → create **sender profile**  
2. Email path: **shared IP pool** (small) **or** register **Email IP Address** endpoint  
3. Import contacts (CSV) with consent fields where required  
4. Create list or segment  
5. Create **Broadcast Campaign** + template (Liquid) → schedule or send now  
6. Delivery: personalize → compliance gate → DKIM → **Email IP node or shared pool** → MX  
7. Opens/clicks tracked; unsub works; suppression updated  

### Slice B — Automation

| Priority | Flow | Depends on |
|---|---|---|
| **P0** | **Welcome series** *or* **double opt-in** | `contact.created` / form + Journey-backed Campaign |
| **P0** | **Cart abandon** | Tracker cart events + **session_expired** + Journey |

MVP launch criterion: **at least one** of welcome/DOI **and** cart abandon is live; the other may follow in the same release train.

### Slice C — Individual rights (minimum)

1. List-Unsubscribe + body unsub  
2. Preference center: unsub, categories if configured, delete request  
3. T1 OTP delete; suppression hash retained  
4. Full multi-brand profile portal not required if single-marketer preference center covers MVP  

### Slice D — Full mining tokenomics (required)

1. **Chain live** (OP Stack L3 on Base per architecture) with native ERC-20 token  
2. **Genesis / emission schedule** encoded and queryable — epoch emission deterministic  
3. **Allocation buckets** live per [token distribution](./Blockchain/token-distribution-and-launch-economics.md) (network rewards, ecosystem, foundation, team, investors, public — exact % may finalize in tokenomics appendix but **mechanism and contracts** ship)  
4. **Node registration + staking** for rewarded types: Storage, OLTP, Analytics, Consensus (as applicable to MVP workloads)  
5. **Epoch loop**: metrics → score → reward share → payout (or claimable reserve on low heartbeat compliance)  
6. **Penalties / slashing** progressive stages operational (at least through demonstrable Stage 1–3 paths in testnet; Stage 4 path documented and enforceable)  
7. **Dynamic reward multipliers** by node type supply/demand (per [node-types-and-rewards](./Platform/node-types-and-rewards.md)) — live, not a spreadsheet  
8. **Tenant payment path**: prepaid credits; storage/egress (and compute if used) burn credits; stablecoin → token settlement if in payment spec  
9. **Email IP nodes explicitly excluded** from emission and multipliers  
10. **Bootstrap**: foundation nodes + cold-start rules so the network can run with few operators initially  

Without Slice D, MVP is **not** done — even if martech Slice A–C work.

---

## MVP feature matrix

### In scope

| Area | MVP includes |
|---|---|
| **Contacts** | Create/update, lists (standard/suppression/seed), basic segments, CSV/batch import jobs, export, erasure hash — see [contact-import-and-lists](./MarketingData/contact-import-and-lists.md) |
| **Campaigns** | Broadcast; Triggered simple; journey-backed for abandon/welcome |
| **Sender profiles** | Multi-sender; shared domain/IP |
| **Email edge** | Shared pool onramp **or** Email IP node; no mining rewards on Email IP |
| **Auth mail** | DKIM, SPF, DMARC, warm-up, bounce/FBL → suppression |
| **Templates** | Liquid; preview/test; starter template subset |
| **Tracking** | JS + pixel, brand cookie, consent, ecom events, session model |
| **Journeys** | Linear + branch + wait; re-entry; drain versioning; activity events |
| **Compliance** | Pre-send gate; marketing vs transactional category |
| **Frequency / quiet hours** | Defaults 2/day, 7/week; quiet hours when TZ known |
| **Analytics** | Preferred: campaign + deliverability; journey operational stats |
| **Identity** | T0/T1 required; T2 nice-to-have |
| **Abuse (minimum)** | Trust tiers, auto-pause, shared-pool isolation — [abuse-and-sender-reputation](./Messaging/abuse-and-sender-reputation.md) |
| **Mining tokenomics (full)** | Emission, stake, epoch rewards, penalties/slash, dynamic type multipliers, tenant credit settlement, vesting/unlock for non-reward allocations as designed |
| **Chain / settlement** | L3 live; soft finality for protocol ops; hard finality for bridges as designed |
| **Event integrity** | Hourly Merkle roots committed on-chain |
| **Rewarded node types** | Storage, OLTP, Analytics, Consensus, **Serverless**, Verifiers (as used); Email IP never rewarded |
| **Node runner tooling** | Install, stake, register, dashboard for rewards/penalties (MVP-grade) |

### Out of scope (reiterated)

Data Cloud product, full integrations matrix, hosted commerce, SMS/push, multi-touch attribution, ad-hoc DuckDB SQL, full malicious-sender ML, L1 migration, decentralized sequencer, Email IP mining.

---

## Pre-MVP infrastructure build order

Before martech application development begins, the following core infrastructure layers must be stood up **in this order**:

**Resequenced per #108** (2026-07-22): Blob Storage's own cold-start procedure (`Requirements/BlobStorage/metadata-architecture.md`) requires a node to generate a keypair and register on-chain, and every epoch to submit a signed Merkle root on-chain — so a minimal bootstrap chain and the wallet-keypair identity primitive must exist no later than concurrent with the first Blob Storage node, not after it. See issue #108 for the full dependency analysis.

| Order | Layer | Notes |
|---|---|---|
| 1 | **Minimal Bootstrap Chain + Wallet-Keypair Identity Primitive + Blob Storage** *(concurrent)* | Blob Storage node cold start requires generating a keypair and registering on-chain, then submitting a signed Merkle root every epoch (issue #12) — so a **minimal** chain (foundation-operated, pre-staked, small trusted verifier set; subset of issue #57 Phase 0) and the **core keypair-generation/registration** primitive (subset of issue #21) must exist alongside the first storage node, not after it. This is a narrow slice, not the full Chain/Tokenomics or full Identity Baseline buildout below |
| 2 | **Identity Baseline (full)** | Consent grants, permission records, and capability tokens tied to the wallet address (full scope of issue #21) plus the embedded custodial/self-custody wallet UX (issue #22) for human onboarding — builds on the keypair primitive already established in step 1; threads consent/individual-rights through tracking, contacts, and compliance gates before martech application development begins |
| 3 | **Queue / Pub-Sub** (NATS JetStream) | Event-integrity pipeline needs this to batch events before hourly Merkle commitment to chain |
| 4 | **Bootstrap / Cold Start (full)** *(concurrent with 5)* | Full phased rollout (private testnet → public testnet → mainnet launch, issue #57 Phases 1–3) beyond the minimal Phase 0 slice already delivered in step 1 |
| 5 | **Chain (full)** *(concurrent with 4)* | Staking contracts, full decentralized verifier-quorum growth, governance bootstrap — beyond the minimal chain already delivered in step 1. Bootstrap and Chain remain two faces of the same launch effort for their full buildout, not sequential |
| 6 | **Full Mining Tokenomics** | Emission, epoch scoring, staking/slashing, dynamic reward multipliers — live economic layer for rewarded node types |
| 7 | **OLTP** | Capstone, not a peer to Storage — consumes Blob (WAL/data files locally, bulk archival to blob) and joins the same rewarded-node economy as everything below it; the app's actual data-writing capability comes online last |

---

## Technical baseline for MVP deploy

| Layer | MVP approach |
|---|---|
| **App / API** | .NET marketing APIs as specified |
| **OLTP** | Postgres for martech; may run on OLTP compute nodes **or** foundation-operated nodes that still participate in the same stake/reward rules where applicable |
| **Blob** | Decentralized storage network with rewarded Storage nodes (foundation nodes count at cold start) |
| **Bus** | NATS JetStream |
| **Email** | Delivery pipeline + tenant Email IP node or shared pool (**non-mining**) |
| **Chain** | **Required** — L3 on Base, token, staking, rewards, slashing, governance bootstrap (multi-sig OK) |
| **Tokenomics** | **Required** — emission curve, epoch settlement, allocation contracts/vesting, public parameters |
| **Payments** | Prepaid credits; native token and/or stablecoin swap per [payment + billing](./Platform/payment-and-stablecoin-integration.md) / [retention-and-billing](./Platform/retention-and-billing.md) |

Foundation-operated nodes are allowed for cold start but must use the **same economic rules** (stake, metrics, rewards, penalties) as independent runners — no silent infinite free capacity outside the protocol.

---

## Launch checklist (definition of done)

### Martech

- [ ] Domain + ≥1 sender profile  
- [ ] Shared pool **or** Email IP path with real MX delivery  
- [ ] CSV import + Broadcast send  
- [ ] Unsub + suppression  
- [ ] Tracker + session expire + cart abandon Journey  
- [ ] Welcome or DOI Journey  
- [ ] Bounce → suppression  
- [ ] Basic campaign/deliverability stats  
- [ ] Marketer ToS/AUP for email  

### Mining tokenomics

- [ ] Token contract live on L3; total supply / emission schedule fixed at genesis (or governance-locked)  
- [ ] Network rewards bucket pays out only via epoch mechanism (not admin mint to runners)  
- [ ] Node runner can stake, register, and receive epoch rewards for at least **Storage** (and other rewarded types in use)  
- [ ] Dynamic reward multipliers update by type supply/demand  
- [ ] Missed heartbeats / penalty stages reduce rewards; slash path enforceable  
- [ ] Email IP nodes register (if used) and **receive zero mining rewards**  
- [ ] Tenant can purchase credits and consume them for metered usage; settlement visible  
- [ ] Public docs: stake minimums (or formula), epoch length, factor weights, emission schedule  
- [ ] Testnet→mainnet: at least one full epoch seal with non-zero multi-node reward distribution proven  

---

## Staging of post-MVP (next trains)

| Train | Focus |
|---|---|
| **MVP+1** | Browse abandon, back-in-stock/price-drop, RSS newsletter, template library polish, import jobs |
| **MVP+2** | Webhooks, rate limits, CDN assets, T2 portal, malicious-sender v1, more independent node geographic diversity |
| **MVP+3** | ETL connectors, ad audience sync, Data Cloud tier, decentralized sequencer roadmap, CEX listing (not protocol-required) |

Mining tokenomics are **not** deferred to these trains.

---

## Spec work for Slice D

**Economic parameters for MVP are locked** in [Blockchain/tokenomics-mvp.md](./Blockchain/tokenomics-mvp.md) (supply, allocation, emission, stakes, fees, type pools).

Remaining Slice D work is **implementation + legal**, not open product design:

| Item | Status |
|---|---|
| Genesis contracts + epoch distributor | Engineering |
| Testnet multi-node epoch seal | Engineering / ops |
| Token classification / public distribution legal | Counsel |
| Testnet contributor reward formula detail | Publish with testnet docs |

---

## Risks accepted in MVP

| Risk | Mitigation |
|---|---|
| Shared pool email abuse | Caps; review; graduate to own IP |
| Network cold start (few nodes) | Foundation nodes + bootstrap phases; reduced replica counts disclosed |
| Token price / liquidity at launch | Stablecoin payment path; DEX pool on Base; public emission transparency |
| Erasure pseudonymization not court-tested | Legal research parallel; EU gated on counsel |
| Thin RBAC | Single admin per tenant OK |
| Analytics thin | Correctness of send/rewards > dashboards |
| Spec gaps on exact emission/stake numbers | **Must close before mainnet**; see table above |

---

## References

| Topic | Doc |
|---|---|
| Campaigns | [Messaging/campaigns.md](./Messaging/campaigns.md) |
| Email + IP + sender profiles | [Messaging/outbound-email-delivery.md](./Messaging/outbound-email-delivery.md) |
| Journeys | [Journeys/journeys.md](./Journeys/journeys.md) |
| Sessions | [Tracking/session-model.md](./Tracking/session-model.md) |
| Tracking | [Tracking/tracking-architecture.md](./Tracking/tracking-architecture.md) |
| Contacts | [MarketingData/contact-database.md](./MarketingData/contact-database.md) |
| Identity | [Identity/identity-proof-and-claim.md](./Identity/identity-proof-and-claim.md) |
| Token + chain | [**tokenomics-mvp.md**](./Blockchain/tokenomics-mvp.md) (authoritative numbers), [blockchain-and-tokenomics](./Blockchain/blockchain-and-tokenomics.md), [chain-architecture](./Blockchain/chain-architecture.md), [token-distribution](./Blockchain/token-distribution-and-launch-economics.md), [governance](./Blockchain/governance.md) |
| Rewards / penalties | [Network/node-runner-incentives-and-penalties.md](./Network/node-runner-incentives-and-penalties.md), [Platform/node-types-and-rewards.md](./Platform/node-types-and-rewards.md) |
| Billing / payments | [Platform/retention-and-billing.md](./Platform/retention-and-billing.md), [payment-and-stablecoin-integration](./Platform/payment-and-stablecoin-integration.md) |
| Bootstrap | [Network/network-bootstrapping-and-cold-start.md](./Network/network-bootstrapping-and-cold-start.md) |
| Gap backlog | [Todo.md](./Todo.md) |

---

## Decision log

| Decision | Choice |
|---|---|
| Channel | Email only |
| IP model | Shared pool onramp **or** marketer Email IP node |
| Multi-sender | Yes — sender profiles, shared IPs OK |
| Automation | Campaigns + Journeys; abandon via `session_expired` |
| Identity for launch | T1 sufficient for individual rights |
| **Mining tokenomics** | **Required for MVP (full)** |
| Email IP mining | Never |
| Data Cloud / ecom / ads | Post-MVP |
| L1 migration / decen sequencer | Post-MVP |
