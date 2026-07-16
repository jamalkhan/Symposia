# Campaigns

## Overview

A **Campaign** is any email program a marketer runs on Symposia. It is the product-facing noun for "what we send, to whom, and under what rules." Campaigns cover one-shot newsletters, recurring blasts, event-triggered messages, attribute- and segment-driven mail, and multi-step automations (where a Campaign **wraps** a [Journey](../Journeys/journeys.md)).

This document defines:

- Campaign types (Broadcast vs Triggered)
- Message category and **priority** (Marketing / High / Critical) and how they interact with compliance, frequency caps, and quiet hours
- Audience targeting, exclusions, seeds, and freeze semantics
- Scheduling, recurrence, and send-time optimization
- Content binding (templates + snapshots)
- A/B experiments
- Relationship to Journeys, the [delivery pipeline](./outbound-email-delivery.md), and [personalization](./personalization-engine.md)
- Lifecycle, throttling, cancellation, and approval
- v1 channel scope and later-channel placeholders

**v1 channel scope: email only.** SMS, push, and webhook delivery are out of scope for Campaign transport in v1; the model is designed so a future `channel` dimension can be added without rewriting Campaign identity. See [Future Channels](#future-channels-sms-push-webhook).

---

## Core Concepts

| Term | Definition |
|---|---|
| **Campaign** | A named email program: audience (or trigger), content, schedule/trigger rules, compliance category, priority, and experiment config. |
| **Broadcast** | Audience-first: select who, then send now / at a time / on a recurrence. Includes newsletters and calendar promotions (e.g., Memorial Day sale). |
| **Triggered** | Event- or condition-first: each qualifying contact is enrolled when a trigger fires. Includes purchase confirmations, birthdays, segment entry, API enroll. |
| **Simple Triggered** | A Triggered Campaign that sends **exactly one** email per enrollment (no wait/branch). Executed by the Campaign executor directly. |
| **Journey-backed Triggered** | A Triggered Campaign that **wraps a Journey** for multi-step sequences (waits, branches, multiple actions). The Campaign remains the product shell; the Journey is the step graph. |
| **Enrollment** | One contact's instance of a Triggered Campaign (and of its Journey, if journey-backed). |
| **Send job** | A Broadcast execution unit: one run of a one-shot or one occurrence of a recurring series. |
| **Category** | `marketing` or `transactional` — drives **default** compliance expectations and UI warnings. Soft-tagged by the marketer. |
| **Priority** | `marketing` (default) \| `high` \| `critical` — drives frequency-cap / quiet-hour bypass and content-intent screening. Closed allowlist for High/Critical types in v1. |
| **Content snapshot** | Immutable copy of subject/body (and experiment variants) taken at activate/schedule time so later template edits do not rewrite in-flight or historical sends. |

### What You Called Out Originally (Mapped)

| Original framing | Model |
|---|---|
| Broadcast / blast (newsletter) | **Broadcast**, one-shot or recurring |
| Time-based / scheduled (Memorial Day) | **Broadcast** with `scheduled_at` or calendar recurrence |
| Event-driven (purchase complete) | **Triggered** — event family; often **High** priority if order confirmation |
| Customer-attribute driven (birthday) | **Triggered** — attribute/date family |
| Segmentation entry (Nintendo propensity) | **Triggered** — segment enter/exit family |
| Multi-step automation | **Triggered** + wraps **Journey** |

Nothing important is missing from that list for **why we send**. What this doc adds is the product machinery: priority tiers, freeze policy, A/B, caps/quiet hours, content binding, lifecycle, and the Campaign↔Journey relationship.

---

## Campaign vs Journey

**Campaign is the product noun** for any email program. **Journey is the multi-step execution engine** used when a Triggered Campaign needs more than a single send.

```
┌─────────────────────────────────────────┐
│  Campaign (product shell)               │
│  name, type, priority, category,        │
│  audience/trigger, content, A/B,        │
│  re-entry, approvals, analytics link    │
└──────────────────┬──────────────────────┘
                   │
     ┌─────────────┴─────────────┐
     │                           │
     ▼                           ▼
 Broadcast                  Triggered
 send jobs                  enrollments
     │                           │
     │              ┌────────────┴────────────┐
     │              │                         │
     ▼              ▼                         ▼
 Delivery     Simple: 1 email          Journey-backed:
 pipeline     via Campaign             Campaign.journey_id
              executor                 → Journey executor
```

Rules:

1. A marketer always manages a **Campaign** in the UI/API for email programs.
2. Adding a second step, wait, or branch to a simple Triggered Campaign **attaches or creates a Journey** and sets `execution_mode = journey_backed`. The Campaign ID remains stable.
3. Journey-only objects without a parent Campaign are an internal/advanced escape hatch, not the default marketer path. Prefer Campaign-wrapped Journeys for marketing automation use cases.
4. Journey action "Send email" still produces delivery events attributed to the **parent Campaign** (and step) for analytics.

### Journey versioning (drain model)

**Resolved:** when a marketer publishes changes to an active Journey (including via a journey-backed Campaign), the platform uses **version drain**:

1. Publishing creates an immutable **journey version** `N+1` (full step graph snapshot). Version `N` is marked `draining` — no new enrollments.
2. **New enrollments** (new triggers, API enroll) always use the latest **published** version.
3. **In-flight enrollments** continue on the version they started (`journey_enrollments.journey_version`) until they complete, exit, or are cancelled. They are **not** migrated mid-flight.
4. When zero enrollments remain on version `N`, it becomes `retired`. Historical step execution logs keep the version reference forever.
5. Draft edits do not affect production until explicit publish. Activate/publish is the version boundary.

This is the same policy for stand-alone Journeys and Campaign-wrapped Journeys. See [Journeys — Versioning](../Journeys/journeys.md#journey-versioning).

**Whole-path A/B** (below) materializes each experiment variant as its own published journey version (or version subgraph) under the parent Campaign; drain rules apply per variant version.

---

## Campaign Types

v1 collapses all programs into **two types**.

### 1. Broadcast

**When to use:** Fixed or recurring audience send — newsletters, sales, announcements.

| Property | Behavior |
|---|---|
| Audience | Required: one or more **lists** and/or **segments**, plus optional exclusions and seed list |
| Trigger | None |
| Schedule | `send_now` \| `scheduled_at` \| `recurrence` (see [Scheduling](#scheduling-broadcast)) |
| Freeze | Configurable: freeze at schedule time or at send-job start (see [Audience Freeze](#audience-freeze-broadcast)) |
| Multi-message | Not via Broadcast. Use Triggered + Journey for sequences. |
| Re-entry | N/A (each send job is independent; recurrence creates new jobs) |

### 2. Triggered

**When to use:** Per-contact reaction to something that happened or became true.

| Property | Behavior |
|---|---|
| Audience | Optional **eligibility** segment/list (must match to enroll); exclusions + seeds still apply |
| Trigger families (all v1) | See [Trigger Families](#trigger-families-triggered) |
| Schedule | Fires on trigger; optional delay; quiet hours / STO may shift actual send |
| Execution mode | `simple` (one email) or `journey_backed` |
| Re-entry | Same policies as Journeys (see [Re-entry](#re-entry-triggered)) |

---

## Trigger Families (Triggered)

All four families are supported for **simple** Triggered Campaigns in v1. The same families can start a journey-backed Campaign.

### 1. Platform / contact events

Any event on the tenant event bus (see [Events Catalog](../Events/events-catalog.md), [Queue and Pub/Sub](../Platform/queue-and-pubsub.md)).

Examples: `web.purchase`, `web.add_to_cart`, `email.opened`, `contact.created`, `catalog.product_back_in_stock`.

Configuration: NATS subject or platform event type + optional filter tree (same operators as [Segmentation](../MarketingData/segmentation-engine.md)).

### 2. Contact attribute / date

Evaluated against contact fields and custom properties.

Examples: birthday, anniversary, `properties.loyalty_tier` changed to `Gold`, implied consent expiry approaching.

Date triggers support offset (e.g., 3 days before `properties.birthday`) and timezone resolution (see [Quiet Hours and Timezone](#quiet-hours-and-timezone)).

### 3. Segment enter / exit

Subscribes to `segment.contact_entered` / `segment.contact_exited` for a journey-trigger-enabled segment (see [Segmentation Engine — Membership Events](../MarketingData/segmentation-engine.md#segment-membership-events)).

Example: contact enters "High Nintendo affinity" enrichment segment → Triggered Campaign.

### 4. External API enroll

Marketer systems push enrollments:

```
POST /marketing/campaigns/{id}/enroll
{
  "contact_id": "uuid",
  "context": { ... }   // merge context for personalization / journey
}
```

Subject on bus: `sym.{tenant}.campaign.enroll` (or reuse `sym.{tenant}.journey.enroll` when journey-backed — prefer campaign-scoped subject for simple mode).

---

## Category and Priority

Two orthogonal dimensions. **Both are always visible in UI and API** — selecting a High/Critical `priority_type_code` does **not** hide or hard-lock category.

### Category (`marketing` | `transactional`)

**Soft tagging** with strong defaults and warnings — not a free pass around the law.

| Category | Intent | Compliance behavior |
|---|---|---|
| **marketing** | Promotional / newsletter / nurture | **Full marketing compliance gate** always: unsubscribe required, postal address, consent/CASL/GDPR checks per [Email Compliance](./email-compliance.md), suppression list, category preferences. |
| **transactional** | Message reasonably expected as part of a transaction or account action | **Minimal hard gate**: erasure/suppression-hash, hard-bounce and FBL-complaint suppression. Marketing unsub and marketing consent checks are **not** applied. UI shows warnings if content looks promotional. Misclassification is an AUP concern; platform may flag patterns (volume + promo score). |

Soft means: the marketer chooses the category; the platform does not silently reclassify without notice. Hard means: once category is `marketing`, the full gate always runs. Choosing `transactional` never bypasses erasure or bounce/complaint suppression.

### Category × priority combinations

Any combination is **allowed**, including awkward ones (e.g. `category=marketing` + `priority=critical` + `priority_type_code=otp`).

| Combination | Platform behavior |
|---|---|
| `transactional` + High/Critical allowlisted type | Expected path. Defaults suggested in UI when type is selected; marketer can still change category. |
| `marketing` + High/Critical | **Heavy warnings** (UI banner + API warning codes): frequency/quiet bypass + marketing compliance headers on a “critical” send is unusual and abuse-prone. Requires explicit confirmation checkbox / `acknowledge_unusual_category_priority: true` on activate. Audit-logged. |
| `transactional` + priority `marketing` | Normal for non-allowlisted transactional-ish mail that is not on the High list; subject to caps/quiet hours. |
| `marketing` + priority `marketing` | Default for broadcasts and nurture. |

UI **suggests** `category=transactional` when an allowlisted High/Critical type is chosen, but never auto-locks or hides the field.

### Priority (`marketing` | `high` | `critical`)

Controls **frequency caps**, **quiet hours**, and **content-intent screening**.

| Priority | Frequency cap | Quiet hours | Typical use |
|---|---|---|---|
| **marketing** (default) | Subject to cross-campaign caps | Subject to quiet hours (+ STO) | Newsletters, promos, nurture |
| **high** | Bypasses marketing frequency caps | Bypasses quiet hours | Order/shipping lifecycle |
| **critical** | Bypasses marketing frequency caps | Bypasses quiet hours | Auth and security |

Priority is **not** free-form. v1 uses a **closed allowlist** of campaign type codes. Assigning `high` or `critical` without an allowlisted type is rejected.

#### Critical — closed allowlist (v1)

| Type code | Description |
|---|---|
| `otp` | One-time passcode |
| `magic_link_login` | Passwordless / magic link login |
| `password_reset` | Password reset |
| `mfa_code` | Multi-factor authentication code |
| `security_alert` | Security notification (e.g., new login, password changed) |

#### High — closed allowlist (v1)

| Type code | Description |
|---|---|
| `order_confirmation` | Order placed |
| `shipping_tracking` | Shipped / tracking update |
| `delivery_confirmation` | Delivered |
| `refund_return_confirmation` | Refund or return confirmed |
| `payment_receipt` | Payment receipt / invoice notification |

Everything else defaults to priority `marketing` (even if category is `transactional` — e.g., a mild account notice that is not on the list). Expanding the allowlist is a product/governance change, not a per-tenant config in v1.

#### Content-intent screening (High / Critical)

Before activate/send of a High or Critical Campaign:

1. Platform runs a **score** (rules + AI/heuristic) for promotional / abuse signals: sale language, heavy CTA patterns, list-style marketing, suspicious link sets, known spam patterns, etc.
2. Result is **advisory**: warning in UI/API with score breakdown. Does **not** hard-block by default.
3. Marketer may proceed via an explicit **override** ("big red button"): must confirm intent, acknowledge risk (IP reputation, blacklisting, account review), and the action is **audit-logged** (`campaign.priority_override` with actor, score, timestamp).
4. Pattern abuse (repeated high-score Critical sends, OTP templates with promo bodies, etc.) feeds the [malicious sender controls](#todo-malicious-and-abusive-senders) pipeline.

Critical/High may be created and sent via **UI and API equally**. API callers still hit screening + override attestation fields.

---

## Frequency Capping and Quiet Hours

Applies to **priority = marketing** only. High and Critical bypass both.

### Cross-campaign frequency cap

Platform-enforced cap on marketing email volume **per contact across all Campaigns** (and Journey-backed sends attributed to marketing-priority Campaigns). Same cap applies whether the send originates from a simple Campaign or a Journey action under a Campaign.

| Parameter | Platform default | Tenant-configurable range | Notes |
|---|---|---|---|
| Max marketing emails / contact / rolling 24h | **2** | 1 … **5** | Cannot disable; minimum 1 |
| Max marketing emails / contact / rolling 7d | **7** | 1 … **15** | Cannot disable; minimum 1 |
| Scope | Per tenant | — | All marketing-priority Campaigns for that marketer tenant |
| Cross-tenant (network-wide) | **Not v1** | — | Same individual across Walmart + Hyatt is **not** jointly capped. [Global marketing opt-out](../Identity/subscription-management.md#global-unsubscribe-symposia-level) remains the cross-brand stop. |

When a send would exceed the cap:

- **Broadcast:** contact is skipped for that job with skip reason `frequency_cap`; counted in send report.
- **Triggered simple:** enrollment is delayed until the next window **or** dropped per campaign setting `on_frequency_cap: delay | skip` (default: `delay` up to max wait, then skip).
- **Journey-backed:** send action respects the same rules; wait steps are not "sends." Delay uses the enrollment/step resume path.

**High/Critical override:** only via allowlisted priority types. No generic “priority flag” in v1.

### Quiet hours

Per-contact quiet hours: do not **deliver** marketing email outside an allowed local-time window (default e.g. 08:00–20:00 local).

Timezone resolution order:

1. `contact.timezone` if set  
2. Inferred from address fields (state, postal/ZIP, country) at import or update  
3. Inferred from JS tracker + IP geolocation → timezone mapping (IP not stored long-term; derived timezone may be written to contact — see [Tracking Architecture](../Tracking/tracking-architecture.md) privacy rules)  
4. Tenant default timezone  

If timezone remains unknown, use tenant default (do not invent a contact timezone silently without recording `timezone_source`).

Quiet-hour conflict handling: shift send to next window open (Broadcast job may stagger; Triggered enrollment sets `resume_at`).

---

## Audience

### Broadcast targeting

- **Include:** one or more lists and/or segments (OR within includes, unless marketer selects AND mode — default OR).
- **Exclude:** lists and/or segments (suppression-style: membership removes from audience).
- **Automatic:** tenant suppression list, erasure hashes, compliance skips, frequency/quiet rules as applicable.
- **Seed list:** addresses that always receive a copy of the send (for QA). Seeds bypass audience filters but **do not** bypass erasure/bounce/complaint suppression. Seeds for marketing Campaigns still get unsub links. Seed sends are tagged `seed: true` in events and excluded from primary performance denominators by default (analytics can toggle).

### Triggered eligibility

Optional include segment/list: contact must match at enrollment time. Exclusions and suppression apply at enrollment and again at send time.

### Audience freeze (Broadcast)

Configurable per Campaign (and overridable per send job):

| Mode | Behavior |
|---|---|
| `freeze_at_schedule` | Snapshot contact IDs when the job is scheduled (or recurrence occurrence materializes). Late segment joiners miss this run. |
| `freeze_at_send_start` | Snapshot when the delivery job starts (default; aligns with [Segmentation — campaign targeting](../MarketingData/segmentation-engine.md#segment-based-campaign-targeting)). |

After freeze, **pre-send compliance** still runs per recipient (unsub, bounce, consent expiry can change between freeze and SMTP).

---

## Scheduling (Broadcast)

### Modes (all v1)

| Mode | Config | Notes |
|---|---|---|
| **Send now** | Immediate job | Subject to throttle / warm-up |
| **One-shot schedule** | `scheduled_at` (UTC) + display timezone | Memorial Day sale, product launch |
| **Recurrence** | RRULE-like or structured: daily / weekly / monthly + time + timezone | e.g., every Monday 09:00 tenant TZ |
| **Send-time optimization (STO)** | On/off + window | Per-contact preferred time within a window; see below |

### Recurrence materialization

**Resolved: next occurrence only.**

- An active recurring Broadcast keeps **at most one** non-terminal child send job: the **next** occurrence.
- When that job reaches `completed` or `cancelled`, the scheduler materializes the following occurrence (if the series is still `active`).
- Series `paused` / `archived`: no new jobs; in-flight job may still be cancelled explicitly.
- `occurrence_key` is deterministic (e.g. `campaign_id + scheduled_local_date + sequence`) for idempotency if the worker retries.
- Freeze mode applies at materialization (`freeze_at_schedule`) or at send start (`freeze_at_send_start`) per campaign config — with next-occurrence-only, `freeze_at_schedule` freezes when that single next job is created (typically shortly before or at the schedule boundary, not weeks ahead).

### Send-time optimization (tiered v1)

When STO is enabled for a **marketing-priority** Broadcast:

1. Job freezes audience (per freeze mode).
2. For each contact, compute target local send time using this **priority order**:

| Rank | Source | When used |
|---|---|---|
| 1 | `contact.properties.preferred_send_time` (or dedicated field) | If set and parseable (local time of day) |
| 2 | **Engagement-learned peak hour** | If the contact has ≥ **5** marketing `email_opened` or `email_clicked` events in the last **90 days** (tenant-local). Peak = hour-of-day bucket with highest unique engagement count in contact timezone. Data may come from `marketing.contact_events` or analytics `contact_engagement_snapshot` when available. |
| 3 | Quiet-hours window midpoint | If quiet hours configured for tenant/contact |
| 4 | Tenant default send time | Final fallback |

3. Individual messages are scheduled across the STO window (default **24 hours** from job start / scheduled day, marketer-configurable 4–48h) subject to quiet hours and frequency caps.
4. STO is **off** for High/Critical (send ASAP subject only to delivery rate limits).
5. Learned peaks are **per-contact, per-tenant** only — no cross-tenant learning.

Recurring + STO: each occurrence runs STO independently when its single next job runs.

### Mid-send controls (Broadcast)

| Control | v1 |
|---|---|
| **Cancel** | Yes — stop queueing remaining recipients; already-sent messages stay sent |
| **Throttle** | Yes — reduce send rate (messages/minute) for ISP protection / warm-up alignment |
| **Pause / resume** | **Not v1** — cancel + re-schedule a new job if needed |

---

## Content Model

Campaigns use **both** template reference and snapshot:

1. Marketer selects `template_id` (and optional per-variant templates for A/B) and may override subject Liquid.
2. On **activate** (Triggered) or **schedule / send** (Broadcast), platform writes a **content snapshot**: subject, HTML, text, engine_id, template_id, template_version, merge variable schema hash.
3. Delivery and personalization render from the **snapshot**, not live template edits.
4. Editing the library template after activate does not change in-flight Campaigns. Marketer must create a new Campaign version or explicitly "refresh snapshot" (requires re-approval if tenant policies require it).

Personalization merge context: contact + campaign metadata + journey context (if any) + trigger event payload. See [Personalization Engine](./personalization-engine.md).

Test send and preview use current draft content (not only snapshots).

---

## A/B Experiments

Supported on **Broadcast and Triggered** in v1.

### Broadcast A/B

1. Define 2+ variants (subject and/or content snapshot per variant).
2. Holdout: first N% or first N recipients randomly assigned across variants.
3. Metric: unique opens, clicks, or conversions (configurable; default unique clicks) over a wait window.
4. Auto-winner: after holdout sample + wait window, remaining audience receives winning variant.
5. Manual override: marketer can lock a winner early.

### Triggered A/B — simple (single email)

**Holdout + auto-winner after N enrollments** (not infinite split):

1. First `experiment_sample_size` enrollments randomly assigned to variants (e.g., 50/50).
2. After sample size reached and optional observation window, platform selects winner by metric.
3. Subsequent enrollments receive the winning variant only.
4. If sample never fills (low volume), campaign stays in split mode until manual winner or timeout policy (`keep_splitting` \| `force_variant_a` after T days).

### Triggered A/B — journey-backed (**whole-path**)

**Resolved: each variant is a full path, not only the first email.**

1. Marketer defines 2+ **variant journey versions** (complete step graphs: waits, branches, all email actions). Stored as distinct published journey versions under the same Campaign (`experiment_config.variants[]` → `journey_version_id`).
2. First `experiment_sample_size` enrollments are randomly assigned to a variant; enrollment runs **only that version’s graph** end-to-end.
3. Primary metric for auto-winner is configurable: default **unique email clicks** across any send in the path within the observation window; optional **conversion** (`purchase` / revenue) or **journey completion rate**.
4. After sample size + observation window, platform selects a winning variant version; subsequent enrollments use only the winner’s journey version.
5. Mid-experiment publish of a non-experiment edit follows the [drain model](#journey-versioning-drain-model): do not silently rewrite variant graphs under active sample enrollments — publish new variant versions and start a new experiment or wait for drain.
6. Manual pick-winner API supported.

Experiment assignment (`experiment_variant`, `journey_version`) is stored on the enrollment / send record for analytics.

---

## Re-entry (Triggered)

Same policy vocabulary as [Journeys — Re-entry](../Journeys/journeys.md#re-entry-and-concurrency-policy):

| Policy | Behavior |
|---|---|
| `no_re_entry` | At most one enrollment ever |
| `re_entry_if_not_active` | Only if no active/waiting enrollment |
| `re_entry_after_cooldown` | Cooldown days since last enrollment start |
| `always` | Every qualifying trigger enrolls |

For **simple** Campaigns, "active" means an enrollment with a pending delayed send. For **journey-backed**, active means Journey enrollment statuses `active | waiting_time | waiting_condition`.

---

## Approvals

Optional, tenant-configurable:

| Setting | Behavior |
|---|---|
| `require_approval` | Second authorized user must approve before activate/send |
| `require_approval_above_n` | Approval required when estimated audience ≥ N |
| Neither | Any authorized marketer role can send |

Approval records: approver id, timestamp, campaign version/snapshot id. Rejection returns Campaign to `draft`.

---

## Lifecycle and Status

### Broadcast statuses

```
draft → scheduled → sending → completed
                  ↘ cancelled
         sending → cancelled (partial)
draft → sending (send now)
```

Recurring parent: `active` (series on) / `paused` (no new occurrences) / `archived`. Each occurrence is a child send job with its own status.

### Triggered statuses

```
draft → active → paused → active
              ↘ archived
```

Active means triggers are evaluated. Paused: no new enrollments; in-flight simple delays and Journey enrollments continue unless marketer cancels enrollments.

---

## Data Model (sketch)

```sql
CREATE TABLE marketing.campaigns (
  campaign_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id            UUID NOT NULL,
  name                 TEXT NOT NULL,
  description          TEXT,
  campaign_type        TEXT NOT NULL,  -- broadcast | triggered
  category             TEXT NOT NULL DEFAULT 'marketing',  -- marketing | transactional
  priority             TEXT NOT NULL DEFAULT 'marketing',  -- marketing | high | critical
  priority_type_code   TEXT,           -- otp, order_confirmation, ...; required if priority != marketing
  execution_mode       TEXT NOT NULL DEFAULT 'simple',  -- simple | journey_backed
  journey_id           UUID,           -- required if journey_backed
  status               TEXT NOT NULL DEFAULT 'draft',
  re_entry_policy      TEXT,           -- triggered only
  re_entry_cooldown_days INT,
  on_frequency_cap     TEXT DEFAULT 'delay',  -- delay | skip
  audience_freeze_mode TEXT,           -- freeze_at_schedule | freeze_at_send_start (broadcast)
  schedule_config      JSONB,          -- send_now / scheduled_at / recurrence / sto
  trigger_config       JSONB,          -- triggered: family + filters
  audience_config      JSONB,          -- includes, excludes, seed_list_id
  experiment_config    JSONB,          -- null if none; whole-path variants → journey_version ids
  content_ref          JSONB,          -- template_id(s), subject override
  content_snapshot     JSONB,          -- set on activate/schedule
  screening_score      JSONB,          -- last intent screen result
  priority_override    JSONB,          -- attestation if override used
  unusual_combo_ack    BOOLEAN DEFAULT FALSE, -- marketing+Critical/High acknowledged
  created_at           TIMESTAMPTZ DEFAULT now(),
  updated_at           TIMESTAMPTZ DEFAULT now(),
  created_by           UUID
);

-- Tenant marketing frequency caps (optional overrides within platform bounds)
-- Defaults: 2 / 24h, 7 / 7d; max: 5 / 24h, 15 / 7d


CREATE TABLE marketing.campaign_send_jobs (
  job_id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  campaign_id          UUID NOT NULL REFERENCES marketing.campaigns(campaign_id),
  tenant_id            UUID NOT NULL,
  occurrence_key       TEXT,           -- for recurrence identity
  status               TEXT NOT NULL,  -- scheduled | sending | completed | cancelled
  freeze_mode          TEXT,
  frozen_at            TIMESTAMPTZ,
  audience_count       INT,
  throttle_per_minute  INT,
  started_at           TIMESTAMPTZ,
  completed_at         TIMESTAMPTZ,
  content_snapshot     JSONB,
  experiment_state     JSONB
);

CREATE TABLE marketing.campaign_enrollments (
  enrollment_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  campaign_id          UUID NOT NULL REFERENCES marketing.campaigns(campaign_id),
  contact_id           UUID NOT NULL,
  tenant_id            UUID NOT NULL,
  status               TEXT NOT NULL,  -- pending | delayed | sent | skipped | cancelled | journey_active | ...
  trigger_event_id     UUID,
  context              JSONB,
  experiment_variant   TEXT,
  journey_enrollment_id UUID,
  journey_version      INT,            -- set when journey_backed; drain model
  enrolled_at          TIMESTAMPTZ DEFAULT now(),
  send_at              TIMESTAMPTZ,
  skip_reason          TEXT
);
```

---

## API (sketch)

```
# Campaign CRUD
GET    /marketing/campaigns
POST   /marketing/campaigns
GET    /marketing/campaigns/{id}
PUT    /marketing/campaigns/{id}
POST   /marketing/campaigns/{id}/activate
POST   /marketing/campaigns/{id}/pause
POST   /marketing/campaigns/{id}/archive
POST   /marketing/campaigns/{id}/approve
POST   /marketing/campaigns/{id}/screen-intent     # run High/Critical scoring

# Broadcast
POST   /marketing/campaigns/{id}/send              # send now (or enqueue)
POST   /marketing/campaigns/{id}/schedule
POST   /marketing/campaigns/{id}/jobs/{job_id}/cancel
PATCH  /marketing/campaigns/{id}/jobs/{job_id}/throttle
GET    /marketing/campaigns/{id}/jobs

# Triggered
POST   /marketing/campaigns/{id}/enroll
GET    /marketing/campaigns/{id}/enrollments
DELETE /marketing/campaigns/{id}/enrollments/{eid}

# Experiments
POST   /marketing/campaigns/{id}/experiments/pick-winner
GET    /marketing/campaigns/{id}/experiments/results

# Stats (detail may live in Analytics)
GET    /marketing/campaigns/{id}/stats
```

Delivery, DKIM, bounce, and suppression remain owned by [Outbound Email Delivery](./outbound-email-delivery.md). Campaigns enqueue messages into that pipeline with `campaign_id`, `job_id` / `enrollment_id`, `priority`, `category`, **`sender_profile_id`** ([multi-sender profiles](./outbound-email-delivery.md#sender-profiles-multi-sender)), and snapshot content. Multiple Campaigns may share one sender profile; multiple profiles may share the same Email IP endpoint(s).

---

## Analytics Attribution

- All delivery and engagement events carry `campaign_id` (and `journey_id` / `enrollment_id` when applicable).
- [Analytics Layer](../Analytics/analytics-layer.md) campaign performance endpoints treat Broadcast jobs and Triggered enrollments under the same `campaign_id`.
- Seed sends excluded from primary rates by default.
- Experiment variant is a breakdown dimension.
- **Automation activity events** (`campaign_*`, `journey_*`, `trigger_matched`) are written to each contact’s event history for branching and audit — see [Journeys — Activity Events](../Journeys/journeys.md#activity-events-and-history-branching).

---

## Integration with Existing Specs

| Spec | Relationship |
|---|---|
| [Outbound Email Delivery](./outbound-email-delivery.md) | Transport, auth, warm-up, DSN, suppression |
| [Email Compliance](./email-compliance.md) | Marketing gate when `category = marketing` |
| [Personalization Engine](./personalization-engine.md) | Render from snapshot + merge context |
| [Journeys](../Journeys/journeys.md) | Multi-step engine under journey-backed Campaigns |
| [Segmentation Engine](../MarketingData/segmentation-engine.md) | Audience, eligibility, segment triggers |
| [Contact Database](../MarketingData/contact-database.md) | Contacts, lists, suppression, erasure |
| [Tracking / Event Schema](../Tracking/event-schema.md) | Trigger events, engagement events |
| [Queue and Pub/Sub](../Platform/queue-and-pubsub.md) | Trigger consumption, campaign events |
| [Use Cases](../UseCases/marketing-automation-use-cases.md) | Cart abandon etc. implemented as Triggered + Journey-backed Campaigns |

---

## Future Channels (SMS / Push / Webhook)

**v1: email only.**

Placeholders for later (do not implement yet):

| Channel | Notes when added |
|---|---|
| **SMS** | Separate consent (`sms_marketing`); Critical OTP SMS likely; Campaign gains `channels[]` or per-channel actions |
| **Push** | Device token management; not a substitute for email compliance |
| **Webhook** | Prefer Journey action or Integrations; if Campaign-level webhook, treat as non-message side effect, not a "send" for frequency caps |

Data model should reserve `channels` or keep channel on the action/snapshot so adding SMS does not fork Campaign identity.

---

## Abuse & sender reputation

**Status: MVP minimum specified** — see **[Abuse Detection & Sender Reputation](./abuse-and-sender-reputation.md)**.

Campaign activate and send path must enforce trust tier, pause/freeze state, Critical/High override budgets, and shared-pool guardrails from that doc. Deeper ML / SOC tooling remains post-MVP.

---

## Open Questions

None remaining from the initial discovery set. New questions should be filed in [Todo.md](../Todo.md) or as follow-on PRs to this doc.

---

## Answered Product Decisions (Discovery Log)

| Decision | Outcome |
|---|---|
| Campaign vs Journey | Campaign = any email program; Journey = multi-step engine underneath |
| Type taxonomy | Collapse to **Broadcast** + **Triggered** |
| Multi-step | Hybrid: simple single email; complex = **Campaign wraps Journey** |
| Transactional | Category on Campaign; soft tag + hard marketing gate when `marketing` |
| Channels | Email v1; SMS/push/webhook placeholders |
| Broadcast schedule | One-shot + recurring + STO |
| Segment freeze | Configurable (`freeze_at_schedule` \| `freeze_at_send_start`) |
| Trigger families | All four: event, attribute/date, segment enter/exit, API |
| A/B | Broadcast + Triggered; Triggered = holdout + auto-winner after N |
| Journey-backed A/B | **Whole-path**: each variant is a full journey version/graph |
| Frequency / quiet hours | Cross-campaign caps + per-user quiet hours; TZ from import or tracker/IP |
| Frequency defaults | **2 / rolling 24h**, **7 / rolling 7d**; tenant max **5/day**, **15/week**; cannot disable |
| Cross-tenant frequency caps | **Not v1**; global opt-out only for cross-brand stop |
| STO v1 | Tiered: preferred_send_time → engagement peak (≥5 eng/90d) → quiet midpoint → tenant default |
| Recurrence jobs | **Next occurrence only** (materialize one child job at a time) |
| Journey versioning | **Drain**: in-flight stay on old version; new enrollments on published N+1 |
| Category × priority UX | **Always show both**; suggest transactional for High/Critical; allow marketing+Critical with heavy warning + ack |
| Priority override | Closed **Critical** and **High** allowlists; screening score + AI warning + audited override |
| Mid-send | Cancel + throttle only |
| Approvals | Optional per tenant; optional required above audience N |
| Content | Template reference **and** snapshot on activate/schedule |
| Audience extras | Suppression + exclusions + seed list |
| Critical/High creation | UI and API equal |
| Screening | Score + AI-driven warning; red-button override; malicious-sender TODO |
