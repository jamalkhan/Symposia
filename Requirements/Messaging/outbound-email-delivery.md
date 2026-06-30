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
- Tenants using a Symposia subdomain for sending (e.g., `mail.malamute.via.symposia.network`) inherit the platform's SPF record.
- Tenants using a custom sending domain must add an SPF include record: `include:spf.symposia.network`.
- The platform publishes and maintains the `spf.symposia.network` record covering all platform sending IPs.
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

**Resolved: every marketer owns their own IP address.** When a marketer onboards to the platform, part of their onboarding process is provisioning one or more dedicated sending IP addresses assigned to their account. Their sending reputation is entirely their own — they cannot be affected by another marketer's behavior, and they cannot affect others. This is the model for any marketer who meets the sending volume threshold for a dedicated IP (see [Shared IP Pool — Small Marketer Definition](#shared-ip-pool--small-marketer-definition) below).

Small marketers who do not yet meet the volume threshold are assigned to the **shared IP pool** — a platform-managed set of IPs shared across vetted, low-volume senders with stricter behavioral guardrails. The shared pool is an onramp, not a permanent tier: as a marketer's volume grows past the threshold, they are migrated to a dedicated IP as part of the account upgrade process.

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

### Dedicated IP Onboarding

When a marketer provisions a dedicated IP — either at onboarding (if above the shared pool threshold) or upon graduating from the shared pool — the onboarding steps are:

1. **IP provisioning**: the platform allocates a dedicated IP address to the marketer's account. The IP is registered with major ISP postmaster programs (Gmail Postmaster Tools, Outlook SNDS, Yahoo) under the marketer's sending domain.
2. **rDNS configuration**: the platform configures reverse DNS for the IP to resolve to the marketer's primary sending domain (e.g., `mail.malamute-adventures.com`). Correct rDNS is a basic deliverability requirement.
3. **SPF record update**: the platform updates `spf.symposia.network` (if using a Symposia subdomain) or provides updated SPF records for the marketer's custom sending domain.
4. **Warm-up enrollment**: the IP is placed into the warm-up schedule (see below). Sends above the daily cap are queued to the next day, not rejected.
5. **Postmaster verification**: for custom sending domains, the marketer is prompted to verify their domain in Google Postmaster Tools (DNS TXT record) and grant the platform read access. This surfaces Google's domain reputation score in the deliverability dashboard.

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

### IP Pools

| Pool Tier | Who | Description |
|---|---|---|
| **Shared Pool** | Small marketers (see definition above) | Platform-managed. IP shared across vetted low-volume senders. Stricter real-time guardrails. Onramp tier only. |
| **Dedicated IP** | All other marketers | Default for any marketer above the shared pool threshold. Single tenant per IP. Full warm-up required. Marketer owns their own reputation. |

IP warm-up schedule (for new IPs, including dedicated):

| Day Range | Max Daily Volume |
|---|---|
| Days 1–3 | 200 |
| Days 4–7 | 500 |
| Days 8–14 | 2,000 |
| Days 15–21 | 10,000 |
| Days 22–30 | 50,000 |
| Day 31+ | Uncapped (subject to rate limits) |

Warm-up is enforced by the sending rate limiter. Attempts to send above the daily cap are queued to the next calendar day.

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
