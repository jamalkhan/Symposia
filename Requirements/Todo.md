# Symposia Requirements — Master Todo

This document tracks known gaps, pending specs, and items to be fleshed out across all requirement areas.

---

## Infrastructure Gaps (identified 2026-06-21)

These gaps were identified after the initial 37-document requirement reorganization.

| Area | Item | Status | Notes |
|---|---|---|---|
| **Messaging** | Email delivery, outbound SMTP, personalization, compliance | **Done** | See Requirements/Messaging/ |
| **Messaging** | Queue / Pub-Sub service for event-driven workloads | **Done** | Requirements/Platform/queue-and-pubsub.md — NATS with JetStream |
| **Platform** | Event integrity and Merkle commitments | **Done** | Requirements/Platform/event-integrity.md — blob + hourly on-chain hash commitments |
| **Platform** | CDN / Edge delivery layer | Not started | Needed for landing pages, email images, ad creative |
| **Platform** | Serverless functions / edge compute | Not started | Lambda-like layer for webhooks, transforms, automation triggers |
| **Platform** | API rate limiting and throttling | Not started | Not in any current document; needed for event ingestion at scale |
| **Platform** | Real-time streaming / WebSockets | Not started | Live dashboards, real-time personalization |
| **Platform** | Workflow orchestration | Not started | Drip sequences, multi-step automation flows, step functions-style state |
| **Analytics** | DuckDB or ClickHouse analytical layer | Stub only | See Requirements/Analytics/todo-notes.md |
| **Database** | Postgres version policy | Not started | Which major versions; upgrade path |
| **Database** | Postgres extensions policy | Not started | PostGIS, pgvector, pg_cron — what operators must provide |
| **Database** | Read replica billing clarification | Open question | Shared page bucket → is intra-service page fetch billed as egress? |
| **Database** | Compute overage handling | Open question | What happens mid-epoch if tenant exhausts compute credits? |
| **Database** | Compute operator compensation split | Open question | Proposed 70/30 operator/platform; needs governance proposal |
| **Blockchain** | Compute node staking minimums | Not started | Specific stake-per-vCPU numbers needed |

---

## Marketing Technology — To Spec

These items were identified 2026-06-30 when the martech focus of the platform was articulated.

### Messaging & Email Delivery
| Item | Status | File |
|---|---|---|
| Outbound SMTP delivery to external providers | **Done** | Requirements/Messaging/outbound-email-delivery.md |
| Liquid personalization engine | **Done** | Requirements/Messaging/personalization-engine.md |
| CAN-SPAM / CASL / GDPR email compliance | **Done** | Requirements/Messaging/email-compliance.md |
| Bounce, complaint, and feedback loop handling | **Done** | (in outbound-email-delivery.md) |
| DKIM/SPF/DMARC for outbound marketing mail | **Done** | (in outbound-email-delivery.md) |

### Marketing Data
| Item | Status | File |
|---|---|---|
| Contact / audience database model | **In Progress** | Requirements/MarketingData/contact-database.md |
| Segmentation engine | **In Progress** | Requirements/MarketingData/segmentation-engine.md |
| Contact import / export (CSV, API) | Not started | |
| List management (suppression, seed lists) | Not started | |

### User Identity & Data Sovereignty
| Item | Status | File |
|---|---|---|
| Data ownership model (users own data; marketers have permission) | **In Progress** | Requirements/Identity/user-data-ownership.md |
| Self-service subscription / preference management | **In Progress** | Requirements/Identity/subscription-management.md |
| Right to delete / right to forget (GDPR Article 17, CCPA) | **In Progress** | Requirements/Identity/right-to-delete.md |
| User profile visibility (which brands have data on me) | **In Progress** | Requirements/Identity/user-profile-visibility.md |
| Cross-tracking consent controls | **In Progress** | Requirements/Identity/user-data-ownership.md |
| **Identity proof and claim** — how individuals prove ownership of email, phone, cookies, device IDs; pre-wallet identity lifecycle | **Needs decision** | Requirements/Identity/identity-proof-and-claim.md — 7 open questions listed; blocks right-to-delete, subscription-management, and profile-visibility specs |

### Integrations
| Item | Status | File |
|---|---|---|
| Integrations overview (lifecycle logging, individual visibility, consent requirements, platform policy) | **Done** | Requirements/Integrations/integrations-overview.md |
| Ad platform integrations (Facebook, Google, TikTok, LinkedIn) | **Stub** | Requirements/Integrations/ad-platform-integrations.md — 4 open questions listed |
| CRM sync (Salesforce, HubSpot, Dynamics) | Not started | — |
| ESP sync (SFMC, Braze, Klaviyo) | Not started | — |
| Data warehouse (Snowflake, BigQuery, Redshift) | Not started | — |
| CDP (Segment, mParticle) | Not started | — |
| Webhooks (generic outbound) | Not started | — |
| Conversion API integrations (Meta CAPI, Google Enhanced Conversions) | Not started | Distinct from audience sync — server-side event signals |
| Platform-level identifier index (enable O(1) marketer discovery at claim time) | Not started | Spec'd in user-profile-visibility.md; needs DB schema in contact-database.md |

### Data Ownership & Stakeholders
| Item | Status | File |
|---|---|---|
| Stakeholder definitions (Individual, Marketer, AppBuilder, Symposia) | **Done** | Requirements/Platform/stakeholders-and-personas.md |
| Identity-layer vs. created/derived-layer ownership model | **In Progress** | Requirements/MarketingData/contact-database.md, Requirements/Identity/user-data-ownership.md |
| **Research jurisdictional privacy law on anonymization/pseudonymization** | **Not started** | See note below — needed to validate the erasure mechanism in contact-database.md |

> **Research item**: The erasure model (see [Erasure and the Created-Data Layer](MarketingData/contact-database.md#erasure-and-the-created-data-layer)) assumes that anonymizing or pseudonymizing created/derived data satisfies a "right to delete" request, rather than requiring hard deletion. This needs to be validated jurisdiction by jurisdiction — different privacy regimes define and accept these mechanisms differently:
> - **GDPR (EU)**: Distinguishes "anonymisation" (data falls outside GDPR scope entirely if re-identification is not reasonably possible) from "pseudonymisation" (Article 4(5) — still personal data, but a recognized risk-reduction measure). Need to confirm pseudonymization alone satisfies Article 17 erasure, or whether full anonymization is required.
> - **CCPA/CPRA (California)**: Has its own statutory definition of "deidentified" data (different bar than GDPR anonymization) and separate treatment of "pseudonymized" data as still personal information in some contexts.
> - **CASL (Canada)**: Primarily consent-based for email, less clear on data erasure/anonymization standards — needs research.
> - **PECR/UK GDPR**: Likely mirrors EU GDPR post-Brexit but should be confirmed separately given diverging UK ICO guidance.
> - **Other emerging state laws (Virginia VCDPA, Colorado CPA, etc.)** and other countries (Brazil LGPD, etc.) as the platform expands.
>
> Outcome needed: a jurisdiction-aware policy matrix for when pseudonymization is sufficient vs. when full anonymization (or hard deletion) is legally required, to drive the policy-decision logic referenced in contact-database.md.

### Tracking & Analytics Collection
| Item | Status | File |
|---|---|---|
| Cookie-based tracking system (JS + pixel) | **In Progress** | Requirements/Tracking/tracking-architecture.md |
| First-party (brand) + Symposia network cookie model | **In Progress** | Requirements/Tracking/tracking-architecture.md |
| Standard event schema (pageview, scroll, focus, redirect) | **In Progress** | Requirements/Tracking/event-schema.md |
| E-commerce event schema (purchase, cart add/remove) | **In Progress** | Requirements/Tracking/event-schema.md |
| Custom event definition by marketers | **In Progress** | Requirements/Tracking/event-schema.md |
| Event data routing (where data lands) | Not started | |
| Attribution modeling | Not started | Future |

---

## Open Architecture Questions

These require decisions before specs can be finalized.

1. ~~**Outbound IP and domain strategy**~~ **Resolved**: Each marketer owns their own dedicated IP address, provisioned at onboarding. Small marketers (under 50K emails/month, under 10K active contacts, monthly+ sending cadence, bounce <2%, complaint <0.08%) use a shared IP pool as an onramp. Shared pool graduates to dedicated IP as volume grows. See [outbound-email-delivery.md — Shared IP Pool definition](./Messaging/outbound-email-delivery.md#shared-ip-pool--small-marketer-definition).

2. **Symposia user identity and wallets**: ~~Is the Symposia-level user identity tied to a blockchain wallet address?~~ **Resolved**: Yes. Symposia identity is a blockchain wallet keypair. Consent grants and capability tokens are on-chain. Non-crypto-native UX is handled by an embedded custodial or self-custody wallet in the Symposia client. See [User Data Ownership](./Identity/user-data-ownership.md) and [Security](./Platform/security.md).

3. **Contact database technology**: The contact database needs to support operational queries (segmentation, filtering), bulk imports, and fast individual lookups. Current candidates: (a) Postgres (already specced) for operations + blob for imports, (b) A specialized contact store built on blob storage with Postgres for indexing. Recommendation leans strongly toward Postgres.

4. **Tracking pixel and JS — MVP or phase 2?**: The tracking system is complex (cookie consent flows, cross-domain coordination, event pipelines). Is this required for the first marketing module launch, or is it post-MVP?

5. **Symposia cookie and consent UX**: When a user visits a site with the Symposia tracker, does Symposia show its own consent banner, or does the marketer's consent mechanism cover Symposia tracking? If both cookies need separate consent, this creates UX complexity.
