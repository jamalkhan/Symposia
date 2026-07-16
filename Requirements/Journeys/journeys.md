# Journeys (Marketing Automation)

## Overview

A **Journey** is a configured sequence of steps that a contact moves through automatically in response to events, conditions, or schedules. Journeys transform the platform from a tool that sends individual emails into a marketing automation engine — capable of responding to behavior in real time, waiting for the right moment, branching based on what a contact does, and carrying context (like cart contents) through the entire sequence.

Journeys sit on top of the [event bus](../Platform/queue-and-pubsub.md) — every trigger is an event that arrives via NATS, every action the Journey takes publishes an event back to the bus.

**v1 scope**: Linear sequences and branching automations. Full state machines (loops, merge points, parallel branches) are deferred until concrete use cases require them.

---

## Core Concepts

| Term | Definition |
|---|---|
| **Journey** | The definition: a directed tree of steps with a trigger, actions, waits, and branches. |
| **Enrollment** | A specific contact's instance of a Journey. One contact can have multiple active enrollments across different Journeys simultaneously. |
| **Step** | A single node in the Journey tree. Types: Trigger, Action, Wait, Branch, Exit. |
| **Trigger** | The entry condition — what causes a contact to be enrolled in a Journey. |
| **Context** | Data captured at enrollment time (from the triggering event) and carried through all steps. Used for personalization (e.g., cart contents in an abandon flow). |
| **Re-entry policy** | Rules governing whether a contact can be enrolled in the same Journey more than once, and how often. |
| **Exit condition** | A condition that, if met at any point during a contact's enrollment, immediately ends their Journey instance. |

---

## Triggers

Triggers define what enrolls a contact in a Journey. Any event on the NATS event bus can be a trigger. All trigger types below are supported in v1.

### Event-Based Triggers

| Trigger | NATS Subject | Notes |
|---|---|---|
| Email sent | `sym.{tenant}.events.email.sent` | |
| Email opened | `sym.{tenant}.events.email.opened` | |
| Email clicked | `sym.{tenant}.events.email.clicked` | Can filter by specific link URL |
| Email bounced | `sym.{tenant}.events.email.bounced` | |
| Email unsubscribed | `sym.{tenant}.events.email.unsubscribed` | |
| Page visited | `sym.{tenant}.events.web.pageview` | Can filter by URL pattern |
| Add to cart | `sym.{tenant}.events.web.add_to_cart` | |
| Purchase completed | `sym.{tenant}.events.web.purchase` | |
| Signup form completed | `sym.{tenant}.events.web.form_submit` | Can filter by form ID |
| Custom event | `sym.{tenant}.events.web.custom` | Filter by `event_name` |
| Contact created | `sym.{tenant}.contact.created` | |
| Contact property changed | `sym.{tenant}.contact.updated` | Filter by property name and/or value |
| External API call | `sym.{tenant}.journey.enroll` | Marketer pushes an event from their own system |

### List and Segment Triggers

| Trigger | Notes |
|---|---|
| Added to list | Contact's `list_memberships` gains a specific list |
| Removed from list | Contact's `list_memberships` loses a specific list |
| Enters segment | Segmentation engine evaluates contact into a segment |
| Exits segment | Segmentation engine removes contact from a segment |

### AI/ML Score Triggers

| Trigger | Notes |
|---|---|
| Score threshold crossed | A contact property (e.g., `properties.purchase_propensity`) rises above or falls below a configured value. Evaluated when the `contact.updated` event fires with the relevant property. |
| Appbuilder model output received | A `contact.updated` event where the `owner_type = 'appbuilder'` and the property matches the configured field. See [Stakeholders](../Platform/stakeholders-and-personas.md). |

### Date/Time Triggers

| Trigger | Configuration | Notes |
|---|---|---|
| **Contact's preferred send time** | Field: `contact.properties.preferred_send_time` (time of day, e.g., `08:00`) | Fires at the specified time in the contact's local timezone. Falls back to marketer's default timezone if contact timezone is unknown. |
| **Anniversary/birthday** | Field: `contact.properties.birthday` or any `DATE` property | Fires annually on the date. Configure how many days before/after to send (e.g., "3 days before birthday"). |
| **Fixed date/time** | Specific calendar date + time | One-time trigger; enrolls all contacts in the target segment at that moment. Behaves like a campaign broadcast but with Journey step logic. |
| **Relative to another event** | "N days after contact was created" | Scheduled from enrollment context data; implemented as a Wait step at Journey start. |

### Absence/Timeout Triggers (the Cart Abandon Pattern)

A timeout trigger fires when an **initiating event occurs but an expected follow-up event does NOT occur within a window**. This is the canonical cart abandon use case:

> "Contact added to cart → no purchase within 1 hour → send abandon email"

This is modeled as:
1. Trigger: `add_to_cart` event → enrollment begins
2. Step 1: Wait up to 1 hour, with **exit condition**: `purchase` event received for same contact
3. If exit condition met → Journey exits (contact bought, no email needed)
4. If wait expires without exit condition → proceed to send abandon email

The timeout window and exit condition are configured on the Wait step, not the trigger. This is consistent — absence detection is always a "wait + exit condition" pattern.

---

## Steps

### Step: Action

An Action step performs an operation immediately when reached.

| Action Type | What It Does |
|---|---|
| **Send email** | Enqueues a message in the delivery pipeline. Template ID + merge context (contact data + Journey context). |
| **Send SMS** | Enqueues an SMS (future channel — see [Delivery Channels Roadmap](../Messaging/outbound-email-delivery.md#delivery-channels-roadmap)). |
| **Send push notification** | Enqueues a push notification (future channel). |
| **Update contact property** | Writes a value to a contact's custom property. E.g., set `properties.journey_stage = "post-purchase"`. |
| **Add to list** | Adds the contact to a specified list. |
| **Remove from list** | Removes the contact from a specified list. |
| **Add to segment** | Manually adds the contact to a static segment. |
| **Send webhook** | HTTP POST to a marketer-configured URL with event payload. For triggering external systems. |
| **Notify marketer** | Internal notification to the marketer's account (e.g., "high-value contact completed Journey"). |

### Step: Wait

A Wait step pauses the enrollment until a condition is met.

| Wait Type | Configuration | Notes |
|---|---|---|
| **Fixed delay** | Duration: `3 days`, `2 hours`, `30 minutes` | Simple countdown from when this step is reached. |
| **Until time of day** | Time + timezone source (`contact.timezone` or marketer default) | "Wait until 8am in the contact's timezone." If it's already past 8am today, waits until 8am tomorrow. |
| **Until date** | Contact property (e.g., `birthday`) + offset (e.g., `-3 days`) | Waits until N days before/after a date stored on the contact. |
| **Until condition** | Event filter or property condition | "Wait until the contact makes a purchase" or "Wait until `properties.loyalty_tier` is set." Maximum wait window required (e.g., "wait up to 7 days; if condition not met, proceed anyway / exit"). |

Every Wait step can optionally carry an **exit condition**: if the specified event or property condition occurs during the wait, the enrollment exits immediately (rather than proceeding to the next step). This is the mechanism for the cart abandon timeout pattern.

### Step: Branch

A Branch step evaluates a condition and routes the enrollment down one of two or more paths.

| Branch Type | Example |
|---|---|
| **Event-based** | "Did the contact open the previous email?" (checks `marketing.contact_events`) |
| **Property-based** | "Is `contact.properties.loyalty_tier` equal to 'Gold'?" |
| **Score-based** | "Is `properties.purchase_propensity` > 0.7?" |
| **Automation history** | "Did this contact complete step X of Journey Y?" / "Receive Campaign C email?" — see [Activity Events and History Branching](#activity-events-and-history-branching) |
| **Random split (A/B)** | "50% → Path A, 50% → Path B" (for testing Journey variants) |
| **Percentage split** | "30% → Path A, 50% → Path B, 20% → Path C" |

Branch conditions use the same filter-tree model as the [Segmentation Engine](../MarketingData/segmentation-engine.md) — the same syntax, the same operators, evaluated against the same contact record **and** that contact’s event history. This is intentional: if you can build a segment for it, you can branch on it in a Journey.

### Step: Exit

An Exit step terminates the enrollment. Enrollments can exit via:
- A configured Exit step in the Journey definition
- A global exit condition defined on the Journey (e.g., "exit if contact unsubscribes from any marketer email")
- A per-Wait exit condition (as above)
- The contact being deleted (right-to-delete request)
- The marketer pausing or archiving the Journey

---

## Journey Context

When a contact is enrolled, the triggering event's payload is captured as the **enrollment context** — a JSON object available for personalization throughout all subsequent steps.

Example: a cart abandon Journey triggered by an `add_to_cart` event captures:

```json
{
  "trigger_event": "web.add_to_cart",
  "triggered_at": "2026-06-30T14:23:00Z",
  "event_data": {
    "cart_id": "cart_abc123",
    "items": [
      { "product_id": "sku_99", "name": "Trail Running Shoes", "price": 129.99, "quantity": 1 }
    ],
    "cart_total": 129.99,
    "cart_url": "https://malamute.com/cart/abc123"
  }
}
```

Templates in Journey steps can reference context via Liquid: `{{ journey.event_data.items[0].name }}` → "Trail Running Shoes". This is the same [Personalization Engine](../Messaging/personalization-engine.md) used for campaigns — Journey context is just an additional namespace in the merge context alongside `contact.*` and `campaign.*`.

---

## Re-entry and Concurrency Policy

Configured per Journey. Options:

| Policy | Behavior | Example Use Case |
|---|---|---|
| **No re-entry** | Contact can only ever have one enrollment (active or completed). | Welcome series — only ever sent once. |
| **Re-entry if not active** | Contact can re-enroll after completing or exiting, but not while active. | Post-purchase follow-up — one per purchase cycle. |
| **Re-entry after cooldown** | Contact can re-enroll only after N days/weeks since last enrollment start. | Cart abandon — limit to once per 90 days. |
| **Always re-enroll** | Every qualifying trigger event creates a new enrollment regardless of existing ones. | Real-time product recommendation trigger. |

A contact can be in multiple *different* Journeys simultaneously with no restriction — re-entry policy only governs a contact's multiple enrollments in the *same* Journey.

When a new trigger event arrives, the Journey executor checks re-entry policy before creating an enrollment:

```sql
SELECT * FROM journey_enrollments
WHERE journey_id = $1 AND contact_id = $2
AND (
  status IN ('active', 'waiting_time', 'waiting_condition')   -- currently active
  OR (policy = 're_entry_cooldown' AND enrolled_at > now() - interval '$cooldown_days days')
  OR (policy = 'no_re_entry')
)
LIMIT 1;
-- If any row returned: do not enroll. Otherwise: create enrollment.
```

---

## Journey Versioning

**Resolved (drain model):** editing and publishing an active Journey never mutates the graph under in-flight enrollments.

1. Each **publish** creates an immutable `journey_versions` row (version number + full step graph snapshot). Draft edits are not live until publish.
2. The journey’s `current_published_version` advances to `N+1`. Prior version `N` is `draining`: **no new enrollments**.
3. New enrollments always attach to `current_published_version`.
4. Existing enrollments keep `journey_version = N` and run that graph until complete, exit, or cancel.
5. When no active/waiting enrollments remain on version `N`, status becomes `retired`.

Campaign-wrapped Journeys use the same rules; whole-path A/B variants are separate published versions under the parent Campaign. See [Campaigns — Journey versioning](../Messaging/campaigns.md#journey-versioning-drain-model).

---

## Data Model

```sql
-- The Journey definition
CREATE TABLE journeys (
  journey_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        UUID NOT NULL REFERENCES tenants(id),
  name             TEXT NOT NULL,
  description      TEXT,
  status           TEXT NOT NULL DEFAULT 'draft',  -- draft | active | paused | archived
  current_published_version INT,                   -- null until first publish
  re_entry_policy  TEXT NOT NULL DEFAULT 'no_re_entry',
  re_entry_cooldown_days INT,                      -- set when policy = 're_entry_cooldown'
  global_exit_conditions JSONB,                    -- e.g., exit if unsubscribed
  parent_campaign_id UUID,                         -- set when owned by a Campaign shell
  created_at       TIMESTAMPTZ DEFAULT now(),
  updated_at       TIMESTAMPTZ DEFAULT now()
);

-- Immutable published graph (drain model)
CREATE TABLE journey_versions (
  journey_id       UUID NOT NULL REFERENCES journeys(journey_id),
  version          INT NOT NULL,
  status           TEXT NOT NULL DEFAULT 'published',  -- published | draining | retired
  graph_snapshot   JSONB NOT NULL,   -- full steps + edges at publish time
  published_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
  published_by     UUID,
  PRIMARY KEY (journey_id, version)
);

-- Steps within a Journey (the tree nodes) — draft working set and/or denormalized from snapshot
CREATE TABLE journey_steps (
  step_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  journey_id       UUID NOT NULL REFERENCES journeys(journey_id),
  journey_version  INT,              -- null = draft working copy; set when belonging to a published version
  step_type        TEXT NOT NULL,    -- trigger | action | wait | branch | exit
  config           JSONB NOT NULL,   -- type-specific config (action type, wait duration, branch conditions, etc.)
  next_steps       JSONB,            -- [{ "step_id": "uuid", "condition": null }] for actions/waits
                                     -- [{ "step_id": "uuid", "condition": {...} }] for branches
  created_at       TIMESTAMPTZ DEFAULT now()
);

-- One row per contact per Journey enrollment
CREATE TABLE journey_enrollments (
  enrollment_id    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  journey_id       UUID NOT NULL REFERENCES journeys(journey_id),
  journey_version  INT NOT NULL,   -- frozen at enroll; drain model — never rewritten mid-flight
  contact_id       UUID NOT NULL REFERENCES marketing.contacts(contact_id),
  tenant_id        UUID NOT NULL,
  status           TEXT NOT NULL DEFAULT 'active',
    -- active | waiting_time | waiting_condition | completed | exited | cancelled
  current_step_id  UUID REFERENCES journey_steps(step_id),
  context          JSONB,           -- trigger event data, carried through all steps
  experiment_variant TEXT,          -- when parent Campaign runs whole-path A/B
  enrolled_at      TIMESTAMPTZ DEFAULT now(),
  last_step_at     TIMESTAMPTZ,
  resume_at        TIMESTAMPTZ,     -- set when status = waiting_time; indexed for scheduler polling
  exit_reason      TEXT,            -- 'completed' | 'global_exit_condition' | 'unsubscribed' | 'deleted' | 'marketer_cancelled'
  exited_at        TIMESTAMPTZ
);

CREATE INDEX idx_enrollments_resume ON journey_enrollments (resume_at)
  WHERE status = 'waiting_time';

CREATE INDEX idx_enrollments_active ON journey_enrollments (journey_id, contact_id, status)
  WHERE status IN ('active', 'waiting_time', 'waiting_condition');

-- Log of every step execution per enrollment (audit trail + debugging)
CREATE TABLE journey_step_executions (
  execution_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  enrollment_id    UUID NOT NULL REFERENCES journey_enrollments(enrollment_id),
  step_id          UUID NOT NULL REFERENCES journey_steps(step_id),
  executed_at      TIMESTAMPTZ DEFAULT now(),
  outcome          TEXT,      -- 'advanced' | 'waited' | 'branched_left' | 'branched_right' | 'exited'
  outcome_data     JSONB      -- branch path taken, action result, wait resume time, etc.
);
```

---

## Execution Engine

The Journey executor is a platform service that consumes from NATS and drives enrollments through their steps.

### Trigger Evaluation

The **trigger evaluator** subscribes to all relevant NATS subjects for a tenant. On each event:

1. Look up all active Journeys for the tenant whose trigger configuration matches this event
2. For each matching Journey, identify the contact from the event payload (`contact_id` or email address)
3. Check re-entry policy — skip if policy blocks enrollment
4. Create an `enrollment` record with `status = 'active'` and the trigger event data as `context`
5. Immediately advance to the first step

### Step Execution

On each step, the executor:

- **Action step**: execute the action (enqueue email send, update property, call webhook), record in `journey_step_executions`, advance to next step immediately
- **Wait step (time-based)**: set `enrollment.status = 'waiting_time'`, set `enrollment.resume_at = now() + delay` (adjusted for contact timezone if applicable), record execution, stop. The scheduler picks it up later.
- **Wait step (condition-based)**: set `enrollment.status = 'waiting_condition'`, record a subscription on the relevant NATS subject + contact_id filter, stop. A condition listener resumes the enrollment when the event arrives (or the max wait window expires).
- **Branch step**: evaluate condition against contact record and enrollment context, select the matching path, record the branch taken, advance to the first step of that path
- **Exit step**: set `enrollment.status = 'completed'`, record `exited_at`

### Scheduler (Time-Based Resumes)

A background worker polls:

```sql
SELECT * FROM journey_enrollments
WHERE status = 'waiting_time'
AND resume_at <= now()
LIMIT 500;
```

For each returned enrollment: advance to the next step. The poll interval is 30 seconds — so time-based waits are accurate to within 30 seconds (sufficient for day-scale delays; not intended for sub-minute precision).

For contact-timezone-aware delivery ("send at 8am in contact's timezone"): the scheduler calculates the exact UTC `resume_at` at enrollment time using the contact's `timezone` field. If the contact's timezone is unknown, fall back to the marketer's configured default timezone.

### Condition Listeners (Condition-Based Waits)

For enrollments in `waiting_condition` status, the executor registers a short-lived NATS consumer filtered to `sym.{tenant}.events.>` where `contact_id = {enrollment.contact_id}`. On each matching event:

1. Evaluate the wait condition against the event payload
2. If met: set `enrollment.status = 'active'`, advance to next step, deregister listener
3. If max wait window has elapsed without condition met: check the step config — either advance to next step anyway ("proceed on timeout") or exit the enrollment ("exit on timeout")

---

## Global Exit Conditions

Marketers can configure exit conditions at the Journey level that apply regardless of which step the contact is currently on:

| Condition | Description |
|---|---|
| Contact unsubscribes | If `email.unsubscribed` fires for this contact during any active enrollment, exit immediately |
| Contact deleted | Right-to-delete request — all active enrollments for this contact are cancelled and purged |
| Contact removed from trigger segment | If the contact exits the segment that qualified them for this Journey |
| Marketer configurable | Any event or property condition, using the same filter-tree syntax as Branch conditions |

Global exit conditions are evaluated by the trigger evaluator on every incoming event, in parallel with trigger matching. An event can both exit an existing enrollment and create a new one (though re-entry policy governs whether the new enrollment proceeds).

---

## Re-entry Policy and the Cart Abandon Pattern Together

A complete cart abandon Journey:

```
Trigger: add_to_cart event
  Re-entry policy: re_entry_after_cooldown (90 days)
  Global exit: contact unsubscribes

  Step 1 [Wait — condition-based]
    Wait up to: 1 hour
    Exit condition: purchase event received for same contact
    On timeout (no purchase): proceed to Step 2
    On exit condition met: exit Journey (bought; no email needed)

  Step 2 [Action — send email]
    Template: "You left something behind"
    Context available: {{ journey.event_data.items }}, {{ journey.event_data.cart_url }}

  Step 3 [Wait — fixed delay]
    Duration: 24 hours

  Step 4 [Branch]
    Condition: did contact make a purchase since enrollment?
      → Yes: Step 5a [Exit — completed]
      → No: Step 5b [Action — send follow-up email "Last chance"]

  Step 5b → Step 6 [Exit — completed]
```

---

## API

```
# Journey management
GET    /marketing/journeys                         List journeys
POST   /marketing/journeys                         Create journey
GET    /marketing/journeys/{id}                    Get journey definition
PUT    /marketing/journeys/{id}                    Update draft working copy; publish creates version N+1 (drain model)
POST   /marketing/journeys/{id}/publish            Publish draft → new immutable version; in-flight stay on old version
POST   /marketing/journeys/{id}/activate           Set status = active; begin trigger evaluation
POST   /marketing/journeys/{id}/pause              Pause trigger evaluation; active enrollments continue
POST   /marketing/journeys/{id}/archive            Deactivate and hide

# Enrollment management
GET    /marketing/journeys/{id}/enrollments        List enrollments (filterable by status, date)
GET    /marketing/journeys/{id}/enrollments/{eid}  Get enrollment detail + step execution log
DELETE /marketing/journeys/{id}/enrollments/{eid}  Cancel an enrollment

# Manual enrollment (for external triggers)
POST   /marketing/journeys/{id}/enroll
{
  "contact_id": "con_abc",
  "context": { ... }   // optional: caller-supplied context data
}

# Contact-level view
GET    /marketing/contacts/{id}/journeys           All active and historical enrollments for a contact

# Operational stats only (not funnels / revenue — see Analytics)
GET    /marketing/journeys/{id}/stats              Live operational snapshot
```

### Operational stats (`GET /marketing/journeys/{id}/stats`)

Lightweight, near-real-time counts for the Journey builder UI — **not** historical funnels or revenue:

```json
{
  "journey_id": "uuid",
  "journey_version": 3,
  "as_of": "2026-07-15T12:00:00Z",
  "enrollments": {
    "active": 1800,
    "waiting_time": 900,
    "waiting_condition": 400,
    "completed_24h": 120,
    "exited_24h": 45
  },
  "by_current_step": [
    { "step_id": "uuid", "step_name": "Wait 1h", "count": 600 }
  ],
  "exit_reasons_24h": { "unsubscribed": 20, "completed": 120, "deleted": 2 }
}
```

Full funnel visualization, step drop-off over a date range, email performance per step, and revenue attribution are owned by the [Analytics Layer](../Analytics/analytics-layer.md#6-journey-performance) (`GET /analytics/journeys/{id}/performance`).

---

## Activity Events and History Branching

**Resolved:** every automation touchpoint writes an event onto the **contact’s activity history** (`marketing.contact_events` + NATS). Branches and segments can filter on full step history, campaign sends, and triggers — not only ad-hoc contact properties.

### Principle

If it happened to a contact on Symposia marketing automation, it is an event on that contact. Properties remain useful for denormalized flags; they are **not** required to reconstruct history.

### Events written (per contact)

All of the following are dual-written: **NATS** (for real-time consumers) and **`marketing.contact_events`** (for history, branching, segmentation, analytics).

| Event type | When | Key `properties` |
|---|---|---|
| `journey_enrolled` | Enrollment created | `journey_id`, `journey_version`, `campaign_id`, `enrollment_id`, `trigger_type`, `experiment_variant` |
| `journey_step_entered` | Enrollment advances to a step | `journey_id`, `journey_version`, `step_id`, `step_name`, `step_type`, `enrollment_id` |
| `journey_step_completed` | Step finishes successfully | `journey_id`, `step_id`, `outcome`, `enrollment_id` |
| `journey_step_failed` | Step fails (e.g. email render error) | `journey_id`, `step_id`, `error_code`, `enrollment_id` |
| `journey_exited` | Enrollment ends | `journey_id`, `exit_reason`, `enrollment_id`, `final_step_id` |
| `journey_reentry_blocked` | Trigger matched but re-entry policy blocked | `journey_id`, `policy`, `campaign_id` |
| `campaign_enrolled` | Triggered Campaign enrollment (simple or journey-backed) | `campaign_id`, `enrollment_id`, `trigger_type` |
| `campaign_send_queued` | Message queued for this contact (Broadcast or Triggered) | `campaign_id`, `job_id` or `enrollment_id`, `variant` |
| `campaign_send_skipped` | Skipped (frequency cap, compliance, etc.) | `campaign_id`, `skip_reason` |
| `campaign_job_included` | Contact frozen into a Broadcast audience snapshot | `campaign_id`, `job_id` |
| `trigger_matched` | Trigger evaluator matched contact (before re-entry check) | `campaign_id` / `journey_id`, `trigger_family`, `trigger_event_id` |

Email lifecycle events (`email_sent`, `email_opened`, …) already include `campaign_id` / journey context from the delivery pipeline — they remain the source of truth for engagement. Automation events above are the source of truth for **program structure** (which step, which enrollment).

### Branch / segment operators on history

Filter-tree additions (Journey Branch + Segmentation):

| Operator | Meaning |
|---|---|
| `has_event` | Contact has ≥1 `contact_events` row matching `event_type` + optional property filters + lookback |
| `has_not_event` | Inverse |
| `has_completed_journey_step` | Shorthand: `journey_step_completed` where `journey_id` + `step_id` (optional `journey_version`) |
| `has_exited_journey` | `journey_exited` with optional `exit_reason` |
| `has_received_campaign` | `email_sent` or `campaign_send_queued` with `campaign_id` |
| `has_completed_campaign_path` | Journey-backed: `journey_exited` with `exit_reason=completed` and `campaign_id` |

Example branch condition (JSON filter tree):

```json
{
  "operator": "AND",
  "conditions": [
    {
      "field": "activity.journey_step_completed",
      "operator": "has_event",
      "value": {
        "journey_id": "uuid-welcome",
        "step_id": "uuid-email-1",
        "within_days": 365
      }
    },
    {
      "field": "activity.email_clicked",
      "operator": "has_not_event",
      "value": { "campaign_id": "uuid-welcome", "within_days": 30 }
    }
  ]
}
```

### Query and performance requirements

- Evaluation uses indexed lookups on `marketing.contact_events (tenant_id, contact_id, event_type, occurred_at DESC)` — already required by the contact DB.
- Property filters on `journey_id`, `step_id`, `campaign_id` should be supported via GIN on `properties` or generated columns / expression indexes for hot keys: `(tenant_id, contact_id, (properties->>'journey_id'), occurred_at)`.
- Lookback window is **required** on history operators (default 365 days, max 7 years / retention). Unbounded “ever” is allowed only with explicit `within_days: null` and may force async evaluation for large branches (same async rules as web-activity segment filters).
- Branch evaluation is per-enrollment, single contact — cost is O(events for that contact in window), not O(tenant).

### Dual write reliability

Automation event writes are **inline with the state transition** (same as email delivery events): enrollment is not committed without a durable `journey_enrolled` intent. Prefer: write enrollment row + emit event in one transaction or outbox pattern so history never diverges from `journey_enrollments` / `journey_step_executions`.

---

## Platform Template Library

**Resolved: ship a starter library in v1.**

Platform-provided Campaign + Journey (or Broadcast) templates marketers can **clone** into their tenant as drafts. Cloning copies the graph/content into tenant-owned objects; later platform template updates do not mutate tenant clones.

### Starter set (v1)

| Template key | Type | Maps to use case |
|---|---|---|
| `cart_abandon` | Triggered + Journey-backed Campaign | [Cart Abandon E2E](../UseCases/cart-abandon.md) |
| `browse_abandon` | Triggered + Journey-backed | [Browse Abandon E2E](../UseCases/browse-abandon.md) |
| `welcome_series` | Triggered + Journey-backed | [Welcome Series E2E](../UseCases/welcome-series.md) |
| `double_opt_in` | Triggered + Journey-backed | [Double Opt-In E2E](../UseCases/double-opt-in.md) |
| `post_purchase` | Triggered + Journey-backed | Post-purchase thank-you + cross-sell (linear) |
| `win_back` | Triggered (segment entry) + Journey | Lapsed engagement segment → re-engagement sequence |
| `birthday` | Triggered (attribute/date) simple or short Journey | Birthday / anniversary |
| `back_in_stock` | Triggered + Journey-backed | [Back in Stock](../UseCases/marketing-automation-use-cases.md#3-back-in-stock) |
| `price_drop` | Triggered + Journey-backed | [Price Drop](../UseCases/marketing-automation-use-cases.md#4-price-drop) |
| `newsletter_weekly` | Broadcast recurring | Weekly newsletter shell (content placeholder) |

### Template API

```
GET  /marketing/templates/library                 List platform templates (filter by category)
GET  /marketing/templates/library/{key}           Get template definition (graph + sample content)
POST /marketing/templates/library/{key}/clone     Clone into tenant → draft Campaign (+ Journey if needed)
{
  "name": "My Cart Abandon",
  "customize": { "brand_name": "Malamute" }
}
```

Clone result: new `campaign_id` (and `journey_id` if applicable) in `draft` status; marketer must bind lists/segments, sending domain, and final copy before activate.

Not a marketplace in v1 — platform-authored templates only. Appbuilder-shared templates are a later phase.

---

## Integration with Event Integrity

Journey enrollment events and step completions are compliance-adjacent operations — particularly exits triggered by unsubscribe or deletion requests. Operational automation events are **not** Merkle-committed by default; compliance streams remain the committed set. See [Event Integrity](../Platform/event-integrity.md).

| Event | NATS Subject | Contact events | Committed |
|---|---|---|---|
| Contact enrolled in Journey | `sym.{tenant}.journey.enrolled` | `journey_enrolled` | No (operational) |
| Journey step executed | `sym.{tenant}.journey.step_executed` | `journey_step_*` | No (operational) |
| Enrollment exited due to unsubscribe | `sym.{tenant}.compliance.unsubscribe_requested` | (compliance + journey_exited) | **Yes** |
| Enrollment cancelled due to deletion request | `sym.{tenant}.compliance.deletion_requested` | (compliance + journey_exited) | **Yes** |

---

## First-Class Use Cases

The following marketing automation use cases are explicitly supported by the Journey engine as first-class platform capabilities. Each is defined in the [Marketing Automation Use Cases](../UseCases/marketing-automation-use-cases.md) document.

| Use Case | Trigger Pattern | Journey Pattern |
|---|---|---|
| [Cart Abandon](../UseCases/marketing-automation-use-cases.md#1-cart-abandon) | `web.add_to_cart` → no purchase | Absence/timeout (fully specced above) |
| [Browse Abandon](../UseCases/marketing-automation-use-cases.md#2-browse-abandon) | `web.pageview` → no cart/purchase | Absence/timeout |
| [Back in Stock](../UseCases/marketing-automation-use-cases.md#3-back-in-stock) | Marketer API push (`product_back_in_stock`) | Immediate notify → wait → branch |
| [Price Drop](../UseCases/marketing-automation-use-cases.md#4-price-drop) | Marketer API push (`product_price_drop`) | Immediate notify → wait → branch |
| [List Signup / Welcome Series](../UseCases/marketing-automation-use-cases.md#5-list-signup-welcome-series) | `contact.created` or `form_submit` | Linear series |
| [Double Opt-In](../UseCases/marketing-automation-use-cases.md#6-double-opt-in) | `contact.created` (pending status) | Wait + condition (confirm click) |
| [Brand Affinity New Release](../UseCases/marketing-automation-use-cases.md#7-brand-affinity--new-release) | Marketer API push (`new_product_release`) | Immediate notify → enrichment-filtered audience |
| [Category Affinity New Release](../UseCases/marketing-automation-use-cases.md#8-category-affinity--new-release) | Marketer API push (`new_product_release`) | Immediate notify → enrichment-filtered audience |

---

## Open Questions

None remaining. Prior resolutions:

1. ~~**Journey versioning**~~ **Drain model** — see [Journey Versioning](#journey-versioning).
2. ~~**Journey analytics depth**~~ **Split ownership** — Journey API = operational stats; Analytics layer = funnels, drop-off, revenue (`/analytics/journeys/{id}/performance`).
3. ~~**Frequency capping**~~ **Cross-Campaign caps** — see [Campaigns](../Messaging/campaigns.md#frequency-capping-and-quiet-hours).
4. ~~**Template library**~~ **Ship starter library in v1** — see [Platform Template Library](#platform-template-library).
5. ~~**History branching**~~ **Full step/campaign/trigger history via contact events** — see [Activity Events and History Branching](#activity-events-and-history-branching).
