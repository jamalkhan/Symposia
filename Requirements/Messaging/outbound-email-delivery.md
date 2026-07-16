# Outbound Email Delivery

## Overview

Outbound email delivery enables marketers to send email from the Symposia platform to recipients on external providers — Gmail, Outlook, Yahoo, ProtonMail, and any SMTP-compatible inbox globally. This is a fundamentally different challenge from inbound receipt (which is already implemented in SymposiaServer): sending at scale requires active management of sender reputation, authentication records, bounce processing, and deliverability signals.

The existing `OutboundRelayService` and `OutboundRelayWorker` in SymposiaInboxWeb provide a basic queue-and-retry sending path via a configured relay host. This spec extends that foundation into a production-grade marketing delivery system.

**Email is the first delivery channel, not the only one.** See [Delivery Channels Roadmap](#delivery-channels-roadmap) below — the same campaign/audience/personalization model is intended to extend to SMS, push notifications, and API-based delivery to external platforms over time. This document specs the email channel in full; later channels will get their own specs but should be expected to plug into the same delivery pipeline shape (queue → pre-send processing → rate limiting → channel-specific transport → delivery event capture).

---

## What Already Exists

From the current codebase (SymposiaInboxWeb):
- `OutboundRelayService`: Queues outbound messages to disk (pending folder), supports retry logic (5 attempts, 5-second poll interval).
- `OutboundRelayWorker`: Background worker that polls the queue and sends via a configured external SMTP relay (host, port, SSL, credentials in `InboxWebOptions`).
- `MailboxController.Compose`: REST endpoint `POST /api/mailbox/compose` that submits a message to the relay queue.

**Gaps in the current implementation for marketing use:**
- No DKIM signing of outbound messages.
- No handling of DSN (delivery status notifications) or bounces.
- No feedback loop (FBL) integration for ISP complaint processing.
- No per-tenant sending domain or IP assignment.
- No rate limiting or throttling for high-volume sends.
- No unsubscribe header injection.
- No warm-up logic for new sending IPs/domains.
- Single relay host — no multi-path delivery or fallback routing.

---

## Sending Authentication (DKIM, SPF, DMARC)

Every outbound marketing message must be authenticated. Without authentication, major ISPs (Gmail, Outlook) will reject or junk-folder the message.

### DKIM (DomainKeys Identified Mail)

DKIM signs the message with a private key whose public key is published in the sending domain's DNS.

Requirements:
- The platform must generate a DKIM key pair per sending domain per tenant.
- The public key is published as a DNS TXT record under `[selector]._domainkey.[sending-domain]`.
- The platform instructs the tenant on the DNS records to add (the platform cannot add them; DNS is tenant-controlled).
- The `OutboundRelayService` (or its replacement) must sign the message with the private key before transmission. Signing must cover: `From`, `To`, `Subject`, `Date`, `Message-ID`, `MIME-Version`, and the message body.
- Key length: RSA 2048-bit minimum. Ed25519 support preferred (shorter signatures, widely supported).
- Key rotation: keys should be rotatable without downtime. Rotation requires publishing the new key in DNS and waiting for TTL expiry before cutting over.

### SPF (Sender Policy Framework)

SPF authorizes which IP addresses may send mail on behalf of the sending domain.

Requirements:
- Tenants on the **shared IP pool** use platform-managed SPF (`include:spf.symposia.network` or equivalent covering shared pool IPs).
- Tenants on **Email IP Address nodes** must publish SPF that authorizes **those node IP(s)** (explicit `ip4:` / `ip6:` and/or a tenant-specific include maintained from registered node IPs). The platform generates the recommended SPF string from the tenant’s registered Email IP Address nodes.
- The platform publishes and maintains records covering **shared pool** IPs only for shared-pool senders — not a substitute for marketer node IPs on the dedicated path.
- SPF alignment: the envelope `MAIL FROM` domain must align with the `From:` header domain (relaxed alignment acceptable).

### DMARC

DMARC ties SPF and DKIM together and tells receiving servers what to do with failures.

Requirements:
- The platform publishes aggregate DMARC reports from receiving ISPs and surfaces them to tenants in deliverability dashboards.
- Tenants must configure a DMARC policy on their sending domain. The platform provides the recommended record.
- Minimum recommended policy: `p=none` (report only) during warm-up; `p=quarantine` in production; `p=reject` as the hardened state.
- The platform generates a per-tenant DMARC aggregate report endpoint so ISP reports can be collected and parsed.

---

## Sending Domains and IP Strategy

**Resolved: every marketer owns their own sending IP path via one or more Email IP Address nodes.** Marketers (tenants) **provide** node(s) that supply at minimum their email sending IP address(es) to the network. Those nodes are the **inbound and outbound proxy** for mail associated with the marketer’s sending domain(s). Reputation stays with the marketer’s IP(s); other marketers cannot pollute them.

See [Email IP Address Nodes](#email-ip-address-nodes) for the full node model (multi-IP, no token rewards, relay-only).

Small marketers who do not yet run their own IP node may use a **shared IP pool** — a platform-managed onramp with stricter guardrails (see [Shared IP Pool](#shared-ip-pool--small-marketer-definition)). Graduating off the shared pool means registering at least one Email IP Address node and binding sending domains to it.

### Shared IP Pool — Small Marketer Definition

A marketer qualifies for the shared IP pool (rather than requiring a dedicated IP) if they meet **all** of the following criteria:

| Criteria | Shared Pool Threshold |
|---|---|
| **Monthly send volume** | Fewer than 50,000 emails per month (≤ ~1,700/day on average) |
| **Active contact list size** | Fewer than 10,000 contacts with `subscription_status = active` |
| **Sending frequency** | Sends at least once per month (purely seasonal or one-off senders cannot maintain shared pool reputation; they require dedicated IPs or a per-send warm-up) |
| **Bounce rate** | Under 2% hard bounce rate over the trailing 30 days |
| **Complaint rate** | Under 0.08% complaint rate over the trailing 30 days (below Gmail's 0.1% warning threshold) |

If a marketer exceeds any of these thresholds, they are migrated off the shared pool. Migration is not instant — the marketer is notified, given a 14-day window to provision their dedicated IP, and their dedicated IP warm-up begins immediately. During warm-up, sends above the daily warm-up cap continue to route through the shared pool until the dedicated IP is warmed.

**Shared pool guardrails**: Because shared pool senders affect each other's reputation, the platform applies stricter real-time controls to shared pool sends than to dedicated IP sends:
- Content spam-score threshold is lower — messages with high spam scores are held for review rather than queued
- Bounce rate above 2% pauses the marketer's sends immediately (rather than triggering an alert)
- Complaint rate above 0.08% pauses sends and requires support review to resume
- Daily send cap on the shared pool per marketer is enforced at 2,000/day regardless of the monthly allowance (burst sending on a shared IP is the fastest way to damage pool reputation)

### Dedicated path: Email IP Address node onboarding

When a marketer runs their own sending IP(s) — at onboarding (if above the shared pool threshold) or upon graduating from the shared pool — the steps are:

1. **Provide an Email IP Address node**: marketer deploys (or designates) a node that announces at least one public IPv4/IPv6 address used for SMTP. See [Email IP Address Nodes](#email-ip-address-nodes).
2. **Bind domains**: associate sending domain(s) with that node (or with a set of nodes for multi-IP). All outbound and inbound mail for those domains is proxied through the bound node(s).
3. **rDNS**: marketer (or their infra provider) configures reverse DNS so the sending IP resolves to the appropriate mail hostname (e.g. `mail.malamute-adventures.com`). Platform verifies rDNS before enabling production volume.
4. **SPF / DKIM / DMARC**: platform generates required DNS records; marketer publishes them. SPF must authorize the Email IP Address node’s IP(s).
5. **Warm-up enrollment**: each new IP on a node enters the warm-up schedule (see below).
6. **Postmaster verification**: marketer verifies domains in Gmail Postmaster Tools / equivalent and may grant the platform read access for deliverability dashboards.

The platform does **not** mint token rewards for these nodes. They exist solely to relay mail.

### Sending Domain Options

**Option A: Symposia subdomain** (default, zero DNS configuration for tenant)
- Tenant's messages send from `[tenant-id].mail.symposia.network`.
- All DNS records (SPF, DKIM, DMARC, rDNS) are managed by the platform.
- Deliverability reputation is partially shared at the domain level but IP-isolated.

**Option B: Custom sending domain** (tenant configures DNS)
- Tenant sends from `mail.their-own-domain.com` or `news.their-own-domain.com`.
- Tenant adds CNAME, SPF include, and DKIM TXT records provided by the platform.
- The platform verifies DNS records before enabling sends from the custom domain.
- DMARC policy on the tenant's root domain must be compatible with the configured sending domain.

**Verification flow for custom domains:**
1. Tenant adds sending domain in the platform UI.
2. Platform generates DNS records to add (CNAME for tracking links, SPF include, DKIM TXT, optional DMARC).
3. Tenant publishes DNS records.
4. Platform polls DNS (every 5 minutes for 24 hours, then every hour) and marks verified when records are detected.
5. First send is only permitted after verification.

Sending domains are **infrastructure** (DNS + auth + IP binding). What the recipient sees as “who sent this” is a **Sender Profile** — see [Sender Profiles](#sender-profiles-multi-sender).

### IP Pools

| Pool Tier | Who | Description |
|---|---|---|
| **Shared Pool** | Small marketers (see definition above) | Platform-managed. IP shared across vetted low-volume senders. Stricter real-time guardrails. Onramp tier only. |
| **Email IP Address node(s)** | All other marketers (and any marketer who opts in early) | Marketer-provided node(s). One or more IPs. Full warm-up required. Marketer owns reputation. Inbound + outbound proxy for bound domains. **No token rewards.** Multiple [sender profiles](#sender-profiles-multi-sender) may share the same node/IP. |

IP warm-up schedule (for new IPs, including marketer-provided Email IP Address nodes):

| Day Range | Max Daily Volume |
|---|---|
| Days 1–3 | 200 |
| Days 4–7 | 500 |
| Days 8–14 | 2,000 |
| Days 15–21 | 10,000 |
| Days 22–30 | 50,000 |
| Day 31+ | Uncapped (subject to rate limits) |

Warm-up is enforced by the sending rate limiter **per mail IP** (all sender profiles using that IP count toward the same cap). Attempts to send above the daily cap are queued to the next calendar day.

---

## Sender Profiles (multi-sender)

### Purpose

Many tenants need **multiple sender identities** under one account: brands, sub-brands, regional offices, product lines, transactional vs marketing From addresses, or agencies sending for several clients under one platform tenant (where policy allows).

**Resolved: the platform supports multi-sender profiles per tenant.** Each Campaign / Journey email action / API send selects a **sender profile**. Profiles may **share** one or more Email IP endpoints and may **share** a sending domain (different local-parts or display names), or use separate domains/IPs when isolation is required.

### Hierarchy

```
Tenant
├── Email IP endpoint(s)     ← sendable/receivable IP path (cluster optional)
├── Sending domain(s)        ← verified DNS (SPF/DKIM/DMARC), bound to IP endpoint(s)
└── Sender profile(s)        ← From identity + compliance identity + routing prefs
         │
         └── used by Campaigns, Journeys, transactional sends, API
```

| Object | Answers |
|---|---|
| **Email IP endpoint** | Which IP(s) touch the internet for this mail? |
| **Sending domain** | Which domain is authenticated (DKIM/SPF/DMARC)? |
| **Sender profile** | Who is this message *from* (address, name, reply-to, postal, defaults)? |

### What a sender profile contains

| Field | Required | Notes |
|---|---|---|
| `sender_profile_id` | yes | UUID |
| `tenant_id` | yes | Owning marketer |
| `name` | yes | Internal label (e.g. "Malamute Marketing", "Malamute Receipts") |
| `from_email` | yes | Must be on a **verified** sending domain for this tenant (or allowed subdomain alias) |
| `from_name` | yes | Display name in the From header |
| `reply_to_email` | optional | Defaults to `from_email` if unset |
| `reply_to_name` | optional | |
| `sending_domain_id` | yes | Domain used for DKIM signing / alignment |
| `postal_address` | yes for marketing | CAN-SPAM physical address; may differ per profile (subsidiary) |
| `default_category` | optional | Suggested `marketing` \| `transactional` for sends using this profile (soft default; Campaign still sets category) |
| `email_ip_endpoint_ids[]` | optional | Prefer these endpoints; if empty, inherit **all** endpoints bound to `sending_domain_id` |
| `routing_strategy` | optional | `inherit` \| `primary_failover` \| `weighted` among the profile’s endpoints |
| `status` | yes | `draft` \| `active` \| `disabled` |
| `locale` / `timezone` | optional | Defaults for that brand voice / scheduling hints |

Liquid/personalization exposes profile fields as `{{ sender.name }}`, `{{ sender.email }}`, `{{ sender.address }}` (already referenced in the personalization engine) — resolved from the **selected sender profile** at send time, not a single tenant-global sender.

### Multi-profile, shared IP (core requirement)

**Many sender profiles may share one or more Email IP addresses.**

| Pattern | Example |
|---|---|
| **Shared IP, shared domain, different From** | `hello@brand.com`, `deals@brand.com`, `news@brand.com` → same domain, same IP endpoint(s) |
| **Shared IP, multiple domains** | `mail.brand-a.com` and `mail.brand-b.com` both bound to the same IP endpoint (common for multi-brand tenants consolidating infrastructure) |
| **Shared IP pool of endpoints, profile prefers subset** | Profiles A/B use endpoints {1,2}; profile C (transactional) uses endpoint {3} only |
| **Isolated IP per profile** | High-risk promo profile on its own warm-up IP; receipts profile on a clean IP |

Sharing IPs is a **first-class, supported** configuration — not an edge case. Isolation is opt-in for reputation or organizational reasons.

```
Sender profile "Newsletter"  ──┐
Sender profile "Promo"       ──┼──► sending domain brand.com ──► Email IP endpoint (IP 203.0.113.10 ± cluster)
Sender profile "Win-back"    ──┘

Sender profile "Order confirms" ──► domain receipts.brand.com ──► same IP endpoint OR different endpoint
```

### Binding rules

1. **`from_email` domain** must match (or be an authorized alias of) the profile’s `sending_domain_id`. Platform rejects activate/send if alignment would break DKIM/DMARC.
2. **IP path:** effective endpoints = profile’s `email_ip_endpoint_ids` if non-empty; else domain-level bindings; else tenant default endpoint. Empty at all levels → cannot send (except shared-pool eligibility).
3. **Multiple profiles → same endpoint:** allowed without limit beyond abuse/rate policy. Warm-up and reputation are tracked **per mail IP** (and optionally reported per domain); volume from all profiles on that IP **sums** toward warm-up and ISP reputation.
4. **Inbound:** bounce/FBL for a domain still hits endpoints bound to that **domain** (or explicit inbound binding). Profiles do not each need a private inbound IP unless the domain is split.
5. **Campaign / Journey / API** must set `sender_profile_id` (or inherit a tenant default profile). Missing profile → send rejected.

### Defaults and onboarding

- On tenant create: one **default sender profile** is created (or required before first send) once a sending domain is verified.
- Additional profiles: unlimited for product purposes; soft limits may apply for abuse (e.g. rate of new From addresses).
- Disabling a profile blocks new sends that reference it; in-flight jobs keep the profile snapshot from queue time.

### Content snapshot

At schedule/activate/send, the delivery pipeline snapshots:

- `from_name`, `from_email`, `reply_to_*`, `postal_address`, `sending_domain_id`, `sender_profile_id`
- Chosen `email_ip_endpoint_id` / mail IP (or selection policy)

so later profile edits do not rewrite in-flight messages (same idea as Campaign content snapshots).

### API (sketch)

```
GET    /marketing/sender-profiles
POST   /marketing/sender-profiles
GET    /marketing/sender-profiles/{id}
PATCH  /marketing/sender-profiles/{id}
POST   /marketing/sender-profiles/{id}/activate
POST   /marketing/sender-profiles/{id}/disable

# Optional: list profiles using a given IP endpoint or domain
GET    /marketing/email-ip-nodes/{id}/sender-profiles
GET    /marketing/sending-domains/{id}/sender-profiles
```

Campaign / send payloads include:

```json
{
  "sender_profile_id": "uuid",
  "campaign_id": "uuid",
  ...
}
```

### Compliance notes

- Each marketing profile needs a valid **postal address** (CAN-SPAM); subsidiaries can differ per profile.
- Unsubscribe and preference-center links remain platform-issued but are scoped to the **tenant + list/category**; From identity is the profile.
- Misrepresenting From (spoofing domains not verified on the tenant) is blocked at send time and is an AUP violation.

### Relationship to Email IP nodes

| Concern | Owned by |
|---|---|
| Who appears in From / Reply-To | **Sender profile** |
| DNS auth (SPF/DKIM/DMARC) | **Sending domain** |
| Wire IP, cluster, inbound/outbound roles | **Email IP endpoint** |
| Many identities, few IPs | **N profiles → 1..M endpoints** (supported) |

---

## Email IP Address Nodes

### Purpose

An **Email IP Address node** is a marketer/tenant-supplied network participant that:

1. **Registers at least one public IP address** used for email (IPv4 required for broad ISP reach; IPv6 optional when dual-stacked).
2. **Relays all outbound SMTP** for sending domains bound to that node (or to the marketer’s node set) — platform → node → recipient MX.
3. **Relays all inbound SMTP** for those domains that the platform must receive on the marketer’s IP — bounces (DSN), FBL/ARF complaints, inbound reply/unsub mailto handling, and any other MX-facing mail the product requires — node → platform processors.

Without at least one healthy Email IP Address node (or shared-pool eligibility), a tenant **cannot** send production marketing volume from a dedicated reputation path.

### Who operates them

| Operator | Notes |
|---|---|
| **Marketer / tenant** | Primary model: brand or their MSP/hosting provider runs the node and owns the IP(s). |
| **Multiple nodes per marketer** | Allowed and expected at scale (geo split, warm-up isolation, reputation separation per brand/subdomain, failover). |
| **Not a mining operator role** | These nodes are **not** Storage / OLTP / Analytics / Consensus workers. |

### Explicit non-goals (rewards)

| Rule | Detail |
|---|---|
| **No token earnings** | Email IP Address nodes **do not earn** native token rewards, epoch emission, or reliability bonuses from the platform mining system. |
| **No stake-for-yield** | Participation is not compensated via staking yield. Any stake/deposit required for abuse prevention (if introduced later) is collateral only, not a mining bond. |
| **Function = relay only** | The **only** function of this node type is outbound and inbound message relay (plus health/heartbeat so the platform can route and fail over). No blob storage, no Postgres, no analytics query serving. |

See [Node Types and Dynamic Rewards — Email IP Address Node](../Platform/node-types-and-rewards.md#email-ip-address-node).

### Minimum requirements

| Requirement | Minimum |
|---|---|
| Public mail IP | ≥ 1 stable **sendable and receivable** public IP registered to the network for the tenant’s mail path (IPv4 required for broad ISP reach; IPv6 optional) |
| Outbound SMTP | Egress to arbitrary recipient MX presents as that public mail IP (single instance **or** cluster behind it) |
| Inbound SMTP | MX / bounce / FBL traffic to that public IP is accepted and handed to the platform |
| Reachability | Platform control plane can reach the node **or cluster control plane** for job dispatch, health checks, and config push |
| Software | Platform-distributed Email IP node agent (container or binary); version-gated for protocol compatibility |
| TLS | TLS for platform↔node control and preferred for SMTP where applicable |
| Auth | Node/cluster identity registered to the tenant; only that tenant’s domains may bind |

What the network and ISPs care about is the **public mail IP** (reputation, SPF, rDNS, MX). How many processes sit behind it is an implementation detail of the marketer’s deployment — see [Clustering](#clustering-behind-a-mail-ip).

### Clustering behind a mail IP

Email IP Address nodes **may be clustered**. Large tenants need local buffering and horizontal capacity for both outbound blast volume and inbound bounce/FBL bursts without exposing every worker as a separate sending IP.

**Requirement:** whatever topology the marketer runs, the IP address **registered to the Symposia network** and used as the tenant’s email sending/receiving identity must be a **real, sendable, and receivable mail IP**:

| Property | Meaning |
|---|---|
| **Sendable** | Outbound SMTP to recipient MX leaves the internet with that IP as the connecting source (or an IP explicitly in the same registered set / SPF). ISPs attribute reputation to this IP. |
| **Receivable** | Inbound SMTP for MX (and bounce/FBL endpoints) can be delivered to that IP; something in the cluster accepts port 25/465/587 as configured and forwards to the platform. |
| **Stable** | Not an ephemeral NAT that changes per connection in a way that breaks rDNS/SPF alignment. |

#### Allowed topologies (illustrative)

```
                    ┌─ worker-1 (agent) ─┐
platform ──► LB / VIP / anycast IP ──────┼─ worker-2 (agent) ─┼──► recipient MX
(public mail IP registered to network)   └─ worker-N (agent) ─┘
         ▲
         │ inbound MX / DSN / FBL
         └── remote MTAs
```

| Topology | Notes |
|---|---|
| **Single host** | One agent, one public IP — fine for smaller dedicated senders. |
| **Active-active cluster** | Multiple agents behind L4/L7 load balancer or shared VIP; **one** (or few) public mail IP(s) registered. Workers share outbound connection pools and inbound accept. |
| **Active-passive HA** | Standby agents; VIP floats on failover; same public IP retained so reputation and DNS do not move. |
| **Multi-IP cluster set** | Cluster A on IP₁, cluster B on IP₂ — still “clustered” per IP; tenant has multiple registered mail IPs for isolation/warm-up. |

#### Buffering (why cluster)

| Direction | Cluster role |
|---|---|
| **Outbound** | Accept relay jobs from the platform faster than MX delivery completes; spool/buffer on cluster disk/memory; pace connections per ISP; absorb Campaign blasts without backing up the whole platform queue on one process. |
| **Inbound** | Absorb bounce/FBL storms after large sends; queue before platform processors; avoid MX deferrals that look like receiver problems. |

Platform-side queues still exist (personalization, compliance, DKIM). Cluster buffering is **additional capacity at the mail edge**, owned by the tenant, so large customers are not limited to a single SMTP process on one box.

#### What is registered vs what is internal

| Registered to Symposia / DNS / SPF / rDNS | Internal only (not required on-chain / not in SPF) |
|---|---|
| Public mail IP(s) | Private worker IPs, pod IPs, east-west mesh |
| Node or **cluster** identity bound to tenant | Autoscaling replicas behind the VIP |
| Health of the **mail IP path** (can send + receive) | Per-worker metrics (optional, for tenant ops) |

The platform may treat a cluster as a single **Email IP endpoint** (one registration, one mail IP, many workers) or as multiple agents that all advertise the **same** `mail_ip` with a shared `cluster_id`. Either model is valid; the invariant is: **egress and ingress for that mail identity use a sendable/receivable IP known to the network.**

#### Constraints

- Open relays and “send from random worker public IPs not in SPF” are forbidden — breaks deliverability and violates registration.
- If the cluster SNATs outbound through a different IP than the registered mail IP, that SNAT IP **must** be the registered one (or an additional registered IP).
- Warm-up and reputation accounting are per **registered mail IP**, not per worker.
- Still **no token rewards** for clusters or workers.

### Inbound vs outbound roles (logical split, flexible deployment)

**Recommendation: split capabilities, not necessarily machines.**

Inbound SMTP (MX, DSN/bounces, FBL-over-SMTP, mailto unsub) and outbound SMTP (relay to recipient MX) have different load shapes, threat models, and failure modes. They should be **first-class roles** on the Email IP edge so large tenants can scale and isolate them. They should **not** be forced into two unrelated products that always require two servers.

| Dimension | Outbound role | Inbound role |
|---|---|---|
| **Job** | Accept platform relay jobs → deliver to recipient MX | Accept internet SMTP on mail IP → forward to platform processors |
| **Load shape** | Bursty with Campaign/Journey blasts; many concurrent egress connections; ISP pacing | Bursty **after** large sends (bounce/FBL storms); many short inbound connections |
| **Threat model** | Abuse of send path, credential theft for open relay | Unsolicited connections, dictionary attacks, inbound floods, spam to bounce addresses |
| **Failure impact** | Sends delay/fail; reputation if forced through wrong IP | Bounce/FBL loss or deferral; compliance/suppression lag; MX reputation as a receiver |
| **Scaling knob** | Egress workers, connection pools, outbound spool | Accept workers, inbound spool, rate-limit by source |

#### What to split (logical)

Each Email IP **endpoint** (registered mail identity) declares which roles it provides:

| Mode | Roles enabled | Typical use |
|---|---|---|
| **`combined`** (default) | Outbound + inbound | Single agent or cluster handles both; fine for most tenants; same process allowed |
| **`outbound_only`** | Outbound only | Dedicated send fleet; inbound handled by another endpoint |
| **`inbound_only`** | Inbound only | Dedicated receive/MX fleet; outbound handled by another endpoint |

A **sending domain** binding must resolve to:

1. At least one healthy endpoint (or cluster) with **outbound** for that domain’s mail IP path, and  
2. At least one healthy endpoint with **inbound** for the domain’s bounce/MX layout (may be the same endpoint).

If either role is missing or all endpoints providing it are down, that direction fails independently (outbound queues; inbound MX defers — monitor both).

#### What not to require (physical)

| Allowed | Not required |
|---|---|
| Both roles in **one process** on one server | Separate VMs for every tenant |
| Both roles on **one cluster** behind one VIP | Separate public IPs for in vs out |
| **Split fleets**: outbound cluster on IP₁, inbound cluster on IP₂ (or same IP with role-specialized pools) | Different node *types* in the mining catalog — still **Email IP Address** edge, role flags only |

**Same server is explicitly supported:** one host runs outbound + inbound roles (combined mode). Large customers **may** deploy separate outbound and inbound pools (possibly still under one registered mail IP via VIP/ports, or under two registered IPs if they want isolation).

#### Same IP vs separate IPs for in/out

| Pattern | When to use |
|---|---|
| **Same sendable/receivable IP for both roles** (recommended default) | Simplest SPF/rDNS/MX story; common MTA practice; one reputation identity |
| **Separate outbound IP vs inbound/MX IP** | Enterprise isolation, different network zones (egress VPC vs DMZ receive), or inbound under heavier attack surface | 

If IPs differ:

- Outbound IP remains the reputation-critical **sending** IP (SPF, warm-up, Postmaster).  
- Inbound IP must still be **receivable** and correctly published in MX for bounce/FBL domains.  
- Return-Path / envelope domains must route inbound to an endpoint that actually accepts that mail.  
- Platform records both IPs on the tenant’s email edge config; warm-up still tracks **sending** IPs.

#### Large-customer pattern (split roles, optional shared IP)

```
                    ┌─ outbound workers (spool + MX egress) ─┐
platform ──► VIP / mail IP  (or dual IP)                    ├──► internet
                    └─ inbound workers  (accept + spool)  ──┘
                         │
                         └──► platform DSN / FBL processors
```

- Scale outbound workers for send volume; scale inbound workers for bounce storms.  
- Buffer each direction independently.  
- Co-locate on one server until metrics justify a split.

#### Product / API shape

```json
{
  "endpoint_id": "uuid",
  "mail_ips": ["203.0.113.10"],
  "roles": ["outbound", "inbound"],
  "deployment": "combined" | "split_roles",
  "cluster_id": "optional"
}
```

Role changes are config updates, not a new node type. Still **no token rewards** for either role.

### Multiple IP address nodes / endpoints per marketer

A tenant may register **N mail endpoints** (each endpoint = one registered mail IP, optionally backed by a cluster of agents).

| Use case | Pattern |
|---|---|
| **Domain isolation** | `news.brand.com` → endpoint A (IP₁ ± cluster); `receipts.brand.com` → endpoint B |
| **Warm-up / new IP** | New mail IP (new endpoint/cluster) while old IP keeps steady volume |
| **Failover** | Secondary endpoint or HA VIP on the same IP |
| **Throughput** | Scale **workers inside a cluster** on one IP; and/or shard across multiple mail IPs |
| **Large-customer buffering** | Cluster behind one sendable/receivable IP for outbound + inbound spool |

**Routing rules:**

- Each **sending domain** binds to one or more registered mail endpoints (IP ± cluster).
- Outbound: delivery pipeline selects a bound endpoint that is healthy and within warm-up/rate limits for the **mail IP**.
- Inbound: DNS MX (or bounce subdomain MX) points at a **receivable** registered mail IP; cluster accepts and forwards.
- Unbound domain → send blocked (or shared-pool only if tenant still eligible).

### Traffic paths

```
OUTBOUND
  Campaign / Journey / API
       → platform delivery queue (personalize, compliance, DKIM)
       → Email IP endpoint (cluster optional; public mail IP)
              └─ buffer / workers ─► recipient MX
                 (source IP = registered sendable mail IP)

INBOUND (bounces, FBL, unsub mailto, etc.)
  remote MTA
       → registered receivable mail IP (MX)
       → cluster accept + buffer
       → platform inbound processors (DSN, FBL, preference)
```

The platform still performs personalization, compliance, DKIM signing, queueing, and event emission. The Email IP edge does **not** need the contact database; it is a **relay + buffer** with tenant isolation and policy hooks (max connections, TLS, reject unauthorized envelope senders).

### Platform responsibilities vs node responsibilities

| Platform | Email IP Address node / cluster |
|---|---|
| Queue, personalize, compliance gate, DKIM | Present **sendable/receivable** public mail IP(s) to the internet |
| Choose endpoint by binding + health + warm-up | Accept outbound relay jobs from platform only; buffer and deliver |
| Parse DSN/FBL after inbound handoff | Accept inbound SMTP on registered IP; buffer; forward to platform |
| Warm-up caps, throttling, cancellation (per mail IP) | Enforce local connection limits; report endpoint + optional worker health |
| Tenant UI for bind/unbind domains | No cross-tenant mail; no arbitrary open relay; SNAT must match registered IP |

### Health, failover, and suspension

- Endpoints (and optionally workers) heartbeat to the platform. Unhealthy endpoints are removed from outbound selection.
- Cluster HA that **keeps the same mail IP** is transparent to the platform if health checks still pass.
- If all endpoints for a domain are down: outbound queues with retry; alerts to marketer; optional temporary shared-pool fallback **only** if policy allows and SPF still aligns (usually **no** — prefer queue/delay over reputation contamination).
- Platform may **suspend** an endpoint/mail IP from sending (spam, abuse, critical bounce/complaint rates) without deleting the tenant account.
- Decommission: marketer drains queues, rebinds domains to another endpoint, then deregisters.

### Registration API (sketch)

```
GET    /marketing/email-ip-nodes
POST   /marketing/email-ip-nodes                 Register endpoint (returns join token / config)
GET    /marketing/email-ip-nodes/{id}
PATCH  /marketing/email-ip-nodes/{id}            Labels, cluster_id, capacity hints
DELETE /marketing/email-ip-nodes/{id}            Deregister (must rebind domains first)
POST   /marketing/email-ip-nodes/{id}/ips        Register mail IP(s) on endpoint
DELETE /marketing/email-ip-nodes/{id}/ips/{ip}
POST   /marketing/email-ip-nodes/{id}/workers    Optional: join additional agents to same cluster/mail IP

GET    /marketing/sending-domains/{id}/ip-bindings
PUT    /marketing/sending-domains/{id}/ip-bindings
{
  "endpoint_ids": ["endpoint_a", "endpoint_b"],
  "strategy": "primary_failover" | "weighted",
  "weights": { "endpoint_a": 80, "endpoint_b": 20 }
}
```

Agent(s) register public key + **mail IP** (the sendable/receivable address). Platform verifies IP ownership via challenge. Additional workers in a cluster join with the same `cluster_id` / `mail_ip` without each needing a distinct public IP.

### Relationship to shared IP pool

| Stage | Mail path |
|---|---|
| Small marketer on shared pool | Platform shared IPs; no Email IP Address node required yet |
| Above threshold / dedicated | **≥ 1 Email IP Address node required** before full dedicated sending |
| Hybrid warm-up | During new-IP warm-up, excess volume may still use shared pool per existing graduation rules — only if still SPF-valid |

---

## Delivery Pipeline

```
Campaign / API trigger
         │
         ▼
  ┌─────────────────┐
  │  Delivery Queue │  ← per-tenant, persistent, prioritized
  │  (Postgres or   │
  │   blob-backed)  │
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │  Pre-Send       │  ← personalization, unsubscribe check,
  │  Processing     │     suppression check, compliance headers
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │  Rate Limiter   │  ← per-tenant, per-IP warm-up, per-domain
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │  DKIM Signer    │  ← signs with tenant's domain key
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │  MX Lookup &    │  ← look up recipient domain MX, route to
  │  SMTP Delivery  │     best MX host, retry on temp failures
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │  DSN Processing │  ← parse bounces, complaints, deferrals
  └────────┬────────┘
           │
           ▼
  Bounce/complaint DB → suppression list update → tenant dashboard
```

### Queue Design

The delivery queue is backed by the tenant's Postgres database (if provisioned) or a platform-managed queue store. Each queued message record contains:
- `send_id`: unique identifier for this delivery attempt
- `campaign_id` / `broadcast_id`: source campaign
- `recipient_address`: the To: email address
- `recipient_contact_id`: reference to the contact record
- `from_address`: the sending address
- `sending_domain_id`: reference to the sending domain config
- `sender_profile_id`: multi-sender identity (From, reply-to, postal); may share IP with other profiles
- `template_id` + `merge_data`: JSON, the merge context for personalization (see [Personalization Engine](./personalization-engine.md))
- `rendered_subject`: final subject line (post-personalization)
- `rendered_html_body` / `rendered_text_body`: final content
- `scheduled_at`: when to send (now or future)
- `attempt_count`: retry counter
- `last_attempt_at`, `next_attempt_at`
- `status`: `pending` | `sending` | `delivered` | `soft_bounce` | `hard_bounce` | `complained` | `failed`

### Retry Logic

| Failure Type | Retry Behavior |
|---|---|
| 4xx SMTP (temp deferral) | Retry with exponential backoff: 5m, 15m, 1h, 4h, 8h. Abandon after 24 hours. |
| 5xx SMTP (hard bounce) | No retry. Record as hard bounce. Add to suppression list. |
| Connection timeout | Retry with alternate MX host. 3 MX hosts attempted before deferral. |
| TLS failure | Retry without STARTTLS (opportunistic TLS only). Log downgrade. |

---

## Bounce and Complaint Processing

### Bounce Types

| Type | Description | Action |
|---|---|---|
| Hard bounce | 5xx permanent failure (bad address, domain doesn't exist) | Immediately add to suppression list. Never retry. |
| Soft bounce | 4xx temporary failure (mailbox full, server down) | Retry per schedule. After 24h without delivery, mark as soft-bounce suppressed. |
| Block bounce | 5xx due to IP/domain reputation block | Flag to deliverability dashboard. May require IP pool change or domain investigation. |

### ISP Feedback Loops (FBL)

Major ISPs allow senders to register for feedback loop reports — notifications when a recipient marks a message as spam.

Requirements:
- The platform registers with all major ISP FBL programs (Gmail Postmaster Tools, Outlook JMRP/SNDS, Yahoo CFL, etc.) for each sending IP range.
- When a complaint arrives via FBL, the recipient is immediately added to the tenant's suppression list.
- The complaint is attributed to the specific campaign/message.
- Complaint rate is surfaced in the tenant's deliverability dashboard.
- A complaint rate above 0.1% (Gmail's threshold) triggers an automatic alert. Above 0.3% triggers a sending pause and requires manual review before resuming.

### List-Unsubscribe Header

Every marketing message sent through the platform must include:
```
List-Unsubscribe: <https://track.symposia.network/unsub/[token]>, <mailto:unsub-[token]@unsub.symposia.network>
List-Unsubscribe-Post: List-Unsubscribe=One-Click
```

The `One-Click` value is required by Gmail (February 2024 bulk sender requirements) and Outlook. It means the ISP can honor a one-click unsubscribe without the recipient needing to visit a web page.

When the platform receives a one-click unsubscribe (via HTTP POST or the mailto address), it immediately:
1. Adds the recipient to the tenant's suppression list.
2. Updates the contact's subscription status in the contact database.
3. Records a timestamped unsubscribe event in the contact's event history.

---

## Event Generation

Every meaningful lifecycle transition of an outbound message generates an event, using the same event pipeline and schema as the rest of the platform (see [Event Schema](../Tracking/event-schema.md)). The delivery pipeline does not maintain its own separate notion of "what happened to this message" — it writes to the same `marketing.contact_events` table that web tracking and other activity write to, so a contact's full activity timeline (email + web + purchase) is queryable in one place.

| Pipeline Stage | Event Emitted |
|---|---|
| Message handed to MX delivery | `email_sent` |
| Receiving MX accepts the message | `email_delivered` |
| Tracking pixel fetched | `email_opened` (with `open_type: human` / `machine`) |
| Tracked link clicked | `email_clicked` |
| 5xx response or terminal soft-bounce timeout | `email_bounced` |
| FBL complaint or one-click unsubscribe received | `email_complained` / `email_unsubscribed` |

These events are not a reporting afterthought — they are the mechanism by which the [Segmentation Engine](../MarketingData/segmentation-engine.md) can target "opened the last campaign but didn't click," the mechanism by which suppression lists and consent records update, and the mechanism by which a future workflow/automation layer (see [Todo.md](../Todo.md)) would trigger follow-up sends. The delivery pipeline diagram above writes to "Bounce/complaint DB → suppression list update → tenant dashboard" — that DB write and the event emission are the same operation, not two separate systems to keep in sync.

Event emission happens inline in the pipeline (DSN Processing step writes the event synchronously with the suppression list update) rather than as an async side effect, so that a contact's status and their event history can never disagree.

---

## Delivery Channels Roadmap

Outbound email is the first delivery channel built, established here as the reference implementation for how the platform models a "send." The same core concepts — a queued message, a personalized payload, pre-send compliance/suppression checks, rate limiting, transport, and delivery event capture — are expected to generalize to other channels as the platform grows. None of the channels below are specced yet; this section exists so the email-specific design choices above aren't made in a way that paints the platform into a single-channel corner.

| Channel | Description | Relationship to Email Spec |
|---|---|---|
| **SMS** | Marketing and transactional text messages | Requires its own consent model (explicit opt-in is mandatory everywhere, no CASL-style implied consent), carrier/aggregator integration (Twilio-like), and character-limit-aware personalization. Suppression and unsubscribe ("reply STOP") follow the same shape as email's suppression list. |
| **Push notifications** | Mobile/web push for tenants with an app or PWA | Requires device token management per contact, platform-specific payload formats (APNs, FCM, Web Push). |
| **API delivery to other ESPs** | Forwarding contacts/events to Salesforce Marketing Cloud, Braze, etc. | Not a "send" in the email sense — this is data egress, letting a marketer keep Symposia as the source of truth for consent/identity while still operating campaigns in an existing ESP. Likely modeled as an outbound webhook/sync integration rather than the delivery queue. |
| **Social ad platforms** | Audience sync to Facebook/Meta, TikTok, etc. for custom-audience advertising | Raises a distinct data-sharing consent question — pushing contact identifiers to a third-party ad platform is a `data_enrichment`-or-stronger permission under [User Data Ownership](../Identity/user-data-ownership.md), not just `email_marketing`. Needs its own consent and audit model before this is built. |
| **CRM sync** | Bi-directional or one-way sync with HubSpot, Salesforce, etc. | Distinct from a delivery channel — this is contact data integration. Likely belongs as its own spec under MarketingData rather than Messaging. |

None of these are scheduled; they are listed here so the delivery pipeline's pre-send processing, suppression, and event-emission steps are designed with the expectation that "channel" will eventually be a dimension, not an assumption baked into the queue schema. See [Todo.md](../Todo.md) for tracking.

---

## Deliverability Dashboard

Tenants need visibility into delivery performance. The deliverability dashboard exposes:

- **Delivery rate**: messages delivered / messages attempted (%)
- **Bounce rate**: hard + soft bounces as a % of sends
- **Complaint rate**: FBL complaints as a % of sends (real-time, not just aggregate)
- **Open rate**: tracked opens (see [Tracking Architecture](../Tracking/tracking-architecture.md))
- **Click rate**: tracked clicks
- **Unsubscribe rate**: unsubscribes triggered from this send
- **Domain-level breakdown**: delivery stats per recipient domain (gmail.com, outlook.com, etc.)
- **IP reputation signals**: IP blacklist status (checked against major RBLs), Google Postmaster domain/IP reputation score
- **DMARC aggregate reports**: pass/fail rates from ISP aggregate reports

API:
```
GET /marketing/domains/{domain-id}/deliverability          Summary for sending domain
GET /marketing/campaigns/{campaign-id}/delivery-stats      Per-campaign stats
GET /marketing/contacts/{contact-id}/delivery-history      Per-contact send history
```

---

## Suppression Lists

A suppression list is a set of addresses that must never receive marketing email from a given tenant, regardless of what lists they appear on.

Categories of suppression:
- **Hard bounced**: added automatically on any 5xx bounce.
- **Complained**: added automatically on any FBL complaint or one-click unsubscribe.
- **Manually unsubscribed**: recipient clicked the unsubscribe link.
- **Globally opted out**: recipient has exercised their Symposia-level "never contact me via any marketer" right (see [User Data Ownership](../Identity/user-data-ownership.md)).
- **GDPR deleted**: recipient submitted a right-to-delete request. Address is retained in suppression list (hashed) so re-import doesn't accidentally re-enable.
- **Manually added by marketer**: tenant adds addresses directly.

Suppression checks happen at the pre-send processing step — not at campaign creation. This ensures that someone who unsubscribes after a campaign is queued but before their message is sent will not receive the message.

---

## Compliance Headers and Required Content

Every marketing message sent via the platform must include:

1. `List-Unsubscribe` and `List-Unsubscribe-Post` headers (see above).
2. A physical mailing address for the sending organization (CAN-SPAM requirement). This is part of the email template and must be configured on the sending domain.
3. An unsubscribe link in the email body. The platform injects this if the template does not include `{{ unsubscribe_url }}`. Templates that explicitly suppress it are rejected.
4. An accurate `From:` name and address — the sender must identify as the real business sending the email.
5. A non-deceptive subject line — the platform does not enforce this technically, but violating this is an AUP violation.

See [Email Compliance](./email-compliance.md) for the full legal framework.

---

## Transactional vs. Marketing Email

The platform supports two categories of outbound email with different rules:

| Property | Transactional | Marketing |
|---|---|---|
| Examples | Password reset, order confirmation, shipping notice | Newsletter, promotional offer, re-engagement |
| Unsubscribe required | No | Yes (mandatory) |
| Suppression list checked | Partial (honor global opt-out; skip others) | Always checked |
| Rate limited | Lower priority queues | Yes |
| DKIM required | Yes | Yes |
| Physical address required | No | Yes |
| Consent basis | Implied (user initiated the action) | Requires prior consent (see [Email Compliance](./email-compliance.md)) |

Marketers tag each send as `transactional` or `marketing` at the send level. Misclassifying marketing sends as transactional is an AUP violation and suppresses required compliance behavior.

---

## Open Questions

- **Direct MX delivery vs. relay**: Should the platform deliver directly to recipient MX servers (full MTA), or always relay through a trusted provider (SparkPost, Postmark, etc.) for the initial version? Direct MX delivery gives the most control but requires building and maintaining IP reputation from scratch. Relay approach is faster to production but adds cost and external dependency.
- **Inbound bounce processing**: Bounces arrive as inbound email to a `bounce+[token]@...` address. This requires the existing inbound SMTP infrastructure to receive and parse bounce DSN messages. Is the existing SymposiaServer sufficient for this, or does it need to be extended?
- **Gmail Postmaster Tools integration**: Requires domain verification via DNS. For Symposia-managed sending domains, the platform registers; for custom domains, the tenant registers and grants the platform read access (via service account). How is this access workflow handled?
