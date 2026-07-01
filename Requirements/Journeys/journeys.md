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
| **Event-based** | "Did the contact open the previous email?" (checks event history) |
| **Property-based** | "Is `contact.properties.loyalty_tier` equal to 'Gold'?" |
| **Score-based** | "Is `properties.purchase_propensity` > 0.7?" |
| **Random split (A/B)** | "50% → Path A, 50% → Path B" (for testing Journey variants) |
| **Percentage split** | "30% → Path A, 50% → Path B, 20% → Path C" |

Branch conditions use the same filter-tree model as the [Segmentation Engine](../MarketingData/segmentation-engine.md) — the same syntax, the same operators, evaluated against the same contact record. This is intentional: if you can build a segment for it, you can branch on it in a Journey.

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

## Data Model

```sql
-- The Journey definition
CREATE TABLE journeys (
  journey_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        UUID NOT NULL REFERENCES tenants(id),
  name             TEXT NOT NULL,
  description      TEXT,
  status           TEXT NOT NULL DEFAULT 'draft',  -- draft | active | paused | archived
  re_entry_policy  TEXT NOT NULL DEFAULT 'no_re_entry',
  re_entry_cooldown_days INT,                      -- set when policy = 're_entry_cooldown'
  global_exit_conditions JSONB,                    -- e.g., exit if unsubscribed
  created_at       TIMESTAMPTZ DEFAULT now(),
  updated_at       TIMESTAMPTZ DEFAULT now()
);

-- Steps within a Journey (the tree nodes)
CREATE TABLE journey_steps (
  step_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  journey_id       UUID NOT NULL REFERENCES journeys(journey_id),
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
  contact_id       UUID NOT NULL REFERENCES marketing.contacts(contact_id),
  tenant_id        UUID NOT NULL,
  status           TEXT NOT NULL DEFAULT 'active',
    -- active | waiting_time | waiting_condition | completed | exited | cancelled
  current_step_id  UUID REFERENCES journey_steps(step_id),
  context          JSONB,           -- trigger event data, carried through all steps
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
PUT    /marketing/journeys/{id}                    Update journey (only drafts; active journeys require a new version)
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

# Analytics (stub — full journey analytics spec TBD)
GET    /marketing/journeys/{id}/stats              Enrollment counts, step completion rates, exit reasons
```

---

## Integration with Event Integrity

Journey enrollment events and step completions are compliance-adjacent operations — particularly exits triggered by unsubscribe or deletion requests. These are emitted to the NATS compliance stream and included in the [Merkle commitment pipeline](../Platform/event-integrity.md):

| Event | NATS Subject | Committed |
|---|---|---|
| Contact enrolled in Journey | `sym.{tenant}.journey.enrolled` | No (operational) |
| Journey step executed | `sym.{tenant}.journey.step_executed` | No (operational) |
| Enrollment exited due to unsubscribe | `sym.{tenant}.compliance.unsubscribe_requested` | **Yes** |
| Enrollment cancelled due to deletion request | `sym.{tenant}.compliance.deletion_requested` | **Yes** |

---

## Open Questions

1. **Journey versioning**: when a marketer edits an active Journey, what happens to contacts currently mid-enrollment? Options: (a) they complete on the old version, (b) they are migrated to the new version at their current step, (c) the journey is versioned and both run simultaneously until old-version enrollments drain. This affects both UX and the data model (`journey_enrollments` needs a `journey_version` reference).

2. **Journey analytics depth**: the `/stats` endpoint is stubbed. Full Journey analytics (funnel visualization, step drop-off rates, revenue attribution per Journey) is a significant feature. Should this be part of the Journey spec or part of the Analytics layer spec?

3. **Frequency capping across Journeys**: a contact could theoretically be in 10 active Journeys simultaneously and receive 10 emails in one day. Should the platform enforce a cross-Journey frequency cap (e.g., max 2 marketing emails per contact per day), and if so, how does that interact with Journey scheduling (does the email get dropped or delayed)?

4. **Journey templates / library**: should the platform ship a library of pre-built Journey templates (welcome series, cart abandon, win-back, post-purchase, birthday)? UX feature, but worth noting as a product decision.

5. **Branching on Journey history**: "did this contact receive email X in a previous Journey enrollment?" requires querying `journey_step_executions` across enrollments. This is possible but query-heavy at scale. Should Journey history be a supported branch condition type, or should marketers use contact properties as a proxy (e.g., update a property when a specific Journey completes)?
