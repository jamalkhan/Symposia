# Segmentation Engine

## Overview

The segmentation engine lets marketers define audiences — filtered subsets of their contact database — based on contact properties, activity history, list membership, enrichment attributes, and compliance status. Segments are used to target campaigns, trigger automations in [Journeys](../Journeys/journeys.md), and sync audiences to ad platforms.

The distinction between **lists** and **segments** is important:
- A **list** is a static collection. Contacts are explicitly added and removed. Membership does not change unless you change it.
- A **segment** is a dynamic filter. Membership is computed from current contact data. It changes automatically as contacts change.

Both can be used as a campaign target. Lists are faster at send time (a simple join). Segments are more powerful but add query cost. The right choice depends on whether the audience definition needs to stay current or was intentionally frozen at a point in time.

---

## Segment Definition

A segment is defined by a **filter tree** — a structured set of conditions combined with AND / OR / NOT logic. The filter tree is serialized as JSON and compiled to parameterized SQL at evaluation time.

### Filter Types

| Category | Operators | Examples |
|---|---|---|
| **Contact field** | eq, neq, contains, starts_with, ends_with, is_empty, is_not_empty, in, not_in | `email ends_with "@gmail.com"`, `country in ["US", "CA"]` |
| **Custom property** | eq, neq, gt, lt, gte, lte, between, is_set, is_not_set | `properties.ltv > 500`, `properties.plan_tier = "Pro"` |
| **Contact enrichment** | eq, neq, gt, lt, gte, lte, contains, is_set, is_not_set | `enrichment.churn_propensity > 0.7`, `enrichment.buyer_profile = "high-value loyalist"` |
| **List membership** | is_in_list, is_not_in_list | `is_in_list "VIP Customers"` |
| **Segment membership** | is_in_segment, is_not_in_segment | `is_not_in_segment "Already Converted"` — see [Suppression Segments](#suppression-segments) |
| **Email activity** | has_received, has_opened, has_clicked, has_not_opened, has_not_clicked | `has_opened any email in last 30 days`, `has_clicked campaign "Summer Sale"` |
| **Web activity** | has_visited_page, has_triggered_event, has_not_triggered_event | `has_triggered_event purchase in last 90 days`, `has_visited_page contains "/pricing"` |
| **Automation history** | has_event, has_not_event, has_completed_journey_step, has_exited_journey, has_received_campaign, has_completed_campaign_path | `has_completed_journey_step welcome/email_1 within 365 days` — backed by contact events from Journeys/Campaigns; see [Activity Events](../Journeys/journeys.md#activity-events-and-history-branching) |
| **Subscription status** | eq | `email_status = subscribed` |
| **Compliance** | expires_within, is_set, is_not_set | `implied_consent_expires_at within 30 days` |
| **Date** | before, after, within, not_within | `created_at after 2026-01-01`, `last_activity_at within 90 days` |
| **Tag** | has_tag, not_has_tag | `has_tag "re-engagement"` |
| **Symposia identity** | is_linked, is_not_linked | `symposia_identity_id is linked` |

**Web activity filters** join against `marketing.contact_events`. They are more expensive than contact-field filters and trigger async evaluation for segments above the size threshold — see [Segment Evaluation](#segment-evaluation).

**Contact enrichment filters** join against `marketing.contact_enrichment` scoped to the tenant's own enrichment attributes. Marketers can filter on any attribute key they have created via the `data_enrichment` permission. Symposia Data Cloud attributes (brand affinity, propensity scores) are a separate feature — see [Data Cloud Segments (Phase 2)](#data-cloud-segments-phase-2).

### Filter Tree Structure

```json
{
  "operator": "AND",
  "conditions": [
    {
      "field": "email_status",
      "operator": "eq",
      "value": "subscribed"
    },
    {
      "field": "country",
      "operator": "in",
      "value": ["US", "CA"]
    },
    {
      "operator": "OR",
      "conditions": [
        {
          "field": "properties.plan_tier",
          "operator": "eq",
          "value": "Pro"
        },
        {
          "field": "properties.ltv",
          "operator": "gt",
          "value": 500
        }
      ]
    },
    {
      "field": "activity.email_opened",
      "operator": "within_days",
      "value": 30
    }
  ]
}
```

### SQL Compilation

The filter tree compiles to parameterized SQL against `marketing.contacts`, with joins added only when a filter type requires them. The engine never generates unbounded queries — `tenant_id` is always the first predicate and indexes are enforced at the query planner level.

The above example compiles to:

```sql
SELECT c.contact_id
FROM marketing.contacts c
WHERE c.tenant_id = $1
  AND c.email_status = 'subscribed'
  AND c.country IN ('US', 'CA')
  AND (
    c.properties->>'plan_tier' = 'Pro'
    OR (c.properties->>'ltv')::numeric > 500
  )
  AND EXISTS (
    SELECT 1 FROM marketing.contact_events e
    WHERE e.tenant_id = $1
      AND e.contact_id = c.contact_id
      AND e.event_type = 'email_opened'
      AND e.occurred_at >= now() - INTERVAL '30 days'
  )
```

An enrichment filter adds a join:

```sql
-- enrichment.churn_propensity > 0.7
AND EXISTS (
  SELECT 1 FROM marketing.contact_enrichment ce
  WHERE ce.tenant_id = $1
    AND ce.contact_id = c.contact_id
    AND ce.attribute_key = 'churn_propensity'
    AND (ce.attribute_value::text)::numeric > 0.7
)
```

SQL generation is parameterized and validated against an allowlist of permitted operators and field paths. User-supplied filter values are always bound parameters — never interpolated into the query string.

---

## Segment Evaluation

### Evaluation Modes

| Mode | When Used | Behavior |
|---|---|---|
| **Synchronous** | Segments with ≤ 500K matching contacts and no web-activity filters | Query runs inline; result returned directly |
| **Asynchronous** | Segments > 500K contacts, or any segment containing a web-activity filter | Job submitted; `job_id` returned; result available via job status endpoint |
| **Estimated count** | Count endpoint (UI preview) | Uses `TABLESAMPLE SYSTEM(5)` for sub-second approximation; exact count computed in background |

The 500K threshold is a soft limit applied per-request. Tenants on higher tiers may have this limit raised. The async path is always available regardless of size — callers can opt into it explicitly.

### Membership Re-evaluation for Journeys

Segments used as [Journey](../Journeys/journeys.md) entry triggers must be evaluated continuously, not just at campaign send time. The platform maintains segment membership for any segment flagged as a journey trigger via a **differential re-evaluation** process:

1. When a contact's record is updated (property change, new event, list membership change), the platform re-evaluates all journey-trigger segments that include a filter on the changed field.
2. If the contact's membership status has changed (entered or exited), a `segment.contact_entered` or `segment.contact_exited` event is published to NATS.
3. Web-activity-based journey-trigger segments are re-evaluated on a **15-minute polling cadence** (real-time event-driven re-evaluation is not practical for join-heavy queries at scale).
4. Simple property-based journey-trigger segments (no event joins) re-evaluate on contact update — effectively real-time.

The platform tracks current segment membership in a materialized table for all journey-trigger segments:

```sql
CREATE TABLE marketing.segment_membership (
  segment_id    UUID NOT NULL,
  tenant_id     UUID NOT NULL,
  contact_id    UUID NOT NULL,
  entered_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (segment_id, contact_id)
);

CREATE INDEX ON marketing.segment_membership (tenant_id, contact_id);
```

This table is maintained by the differential re-evaluation process, not by direct writes from the contact API.

### Segment Membership Events

Published to NATS when a contact enters or exits a journey-trigger segment:

```
Subject: sym.{tenant_id}.segment.contact_entered
Subject: sym.{tenant_id}.segment.contact_exited
```

```json
{
  "event_id": "uuid-v7",
  "event_type": "segment.contact_entered",
  "tenant_id": "uuid",
  "segment_id": "uuid",
  "contact_id": "uuid",
  "occurred_at": "2026-07-01T10:00:00Z"
}
```

The Journey engine subscribes to these events to trigger entry steps. Non-journey segments (used only for campaign targeting or list sync) do not maintain the membership table and do not emit these events — re-evaluation for those happens at campaign send time.

---

## Segment-Based Campaign Targeting

When a campaign is targeted at a segment (rather than a static list), the delivery pipeline:

1. At job creation, runs the segment query and snapshots the resulting contact IDs into the delivery queue. The snapshot is taken at job start, not per-message.
2. The pre-send compliance check still runs per recipient at send time — subscription status, suppression list membership, and consent expiry can change between snapshot and send.
3. Contacts who enter the segment after the snapshot is taken do not receive this campaign. This is intentional: targeting is frozen at job creation.

For very large segments evaluated asynchronously, the snapshot job runs first and campaign scheduling begins only after the snapshot completes.

---

## Suppression Segments

A **suppression segment** is any segment referenced via `is_not_in_segment` in another segment's filter tree. There is no special segment type — this is a compositional filter.

**Example**: "All US subscribers who have NOT already purchased in the last 60 days":

```json
{
  "operator": "AND",
  "conditions": [
    { "field": "country", "operator": "eq", "value": "US" },
    { "field": "email_status", "operator": "eq", "value": "subscribed" },
    {
      "field": "segment",
      "operator": "is_not_in_segment",
      "value": "segment_id_of_recent_purchasers"
    }
  ]
}
```

This compiles to:

```sql
AND c.contact_id NOT IN (
  SELECT contact_id FROM marketing.segment_membership
  WHERE segment_id = 'segment_id_of_recent_purchasers'
    AND tenant_id = $1
)
```

The referenced suppression segment must itself be a journey-trigger segment (so its `segment_membership` table is maintained). Referencing a non-materialized segment as a suppression target is not permitted — the platform validates this at segment save time.

Suppression segments are also valid as a campaign-level suppression target directly in the campaign definition (separate from the targeting segment), which is the simpler and more common pattern.

---

## Segment API

```
GET    /marketing/segments                       List all segments
POST   /marketing/segments                       Create segment
GET    /marketing/segments/{id}                  Get segment definition
PUT    /marketing/segments/{id}                  Update definition
DELETE /marketing/segments/{id}                  Delete segment

GET    /marketing/segments/{id}/count            Count matching contacts
                                                   ?mode=exact|estimated (default: estimated)
GET    /marketing/segments/{id}/preview          Sample up to 25 matching contacts
POST   /marketing/segments/{id}/evaluate         Submit async evaluation job; returns job_id
GET    /marketing/segments/{id}/jobs/{job_id}    Poll async job status and result

POST   /marketing/segments/{id}/sync-list        Materialize segment into a static list (snapshot)
PATCH  /marketing/segments/{id}/journey-trigger  Set/unset as a journey trigger (enables membership tracking)
```

### Count Endpoint

Returns an estimated contact count (sub-second, using table sampling) immediately, and a `job_id` for the exact count running in the background:

```json
{
  "estimated_count": 14200,
  "exact_count": null,
  "exact_count_job_id": "job_uuid",
  "estimated_at": "2026-07-01T10:00:00Z"
}
```

Exact count is available via the job status endpoint once complete.

---

## Common Segment Patterns by Use Case

The following patterns correspond to the [first-class marketing use cases](../UseCases/marketing-automation-use-cases.md). Each shows the filter tree approach for targeting the right audience.

| Use Case | Segment Approach | Filter Type(s) Used |
|---|---|---|
| **Cart Abandon** | All contacts who fired `add_to_cart` in the session — handled by Journey timeout, not a standalone segment | Web activity |
| **Browse Abandon** | Contacts who viewed a specific product/category page — Journey timeout pattern | Web activity |
| **Back in Stock** | Contacts on a restock-notification list for a specific SKU (preferred) OR who viewed the product page | List membership or web activity |
| **Price Drop** | Contacts on a price-watch list for a SKU OR who abandoned a cart containing the product | List membership or web activity |
| **List Signup** | Contacts with `email_status = 'pending'` or newly added to a specific list | Contact field, list membership |
| **Double Opt-In** | Contacts with `email_status = 'pending'` — handled within the Journey | Contact field |
| **Brand Affinity New Release** | Contacts where `enrichment.brand_affinity` score for target brand exceeds threshold | Contact enrichment |
| **Category Affinity New Release** | Contacts where `enrichment.category_affinity` for target category exceeds threshold | Contact enrichment |

For full use case details see [Marketing Automation Use Cases](../UseCases/marketing-automation-use-cases.md).

---

## Data Cloud Segments (Phase 2)

Filtering on Symposia Data Cloud derived attributes (brand affinity, category affinity, propensity scores, demographic inferences) is a **Phase 2 capability**. It is not included in the initial segmentation engine.

The core challenge: Data Cloud attributes are per-individual, gated by the individual's `data_read` permission grant, and only accessible to marketers on the Data Cloud tier. A segment query scanning a million contacts against Data Cloud attributes would require a permission check and a Data Cloud API call per contact — this cannot be compiled to a single SQL query and requires a different evaluation architecture (likely a fan-out to a Data Cloud query service with per-individual permission validation).

When Phase 2 is scoped, the filter type to add is:

```json
{
  "field": "symposia.brand_affinity",
  "operator": "any_gte",
  "value": { "brand": "Nintendo", "score": 0.8 }
}
```

This would require:
- Marketer is on the Data Cloud tier
- Each contact in the candidate set has a linked `symposia_identity_id`
- Each individual has an active `data_read` grant for this marketer
- The Data Cloud query service evaluates the filter and returns matching identity IDs

---

## Relationship to Other Systems

| System | How Segments Connect |
|---|---|
| [Journeys](../Journeys/journeys.md) | Segment entry/exit events trigger journey enrollment and branching |
| [Contact Database](./contact-database.md) | Segments query `contacts`, `contact_events`, `contact_enrichment`, `list_memberships` |
| [Email Delivery](../Messaging/outbound-email-delivery.md) | Campaigns targeted at segments snapshot membership at job creation |
| [Ad Platform Integrations](../Integrations/ad-platform-integrations.md) | Segment membership synced to Facebook Custom Audiences, Google Customer Match, etc. |
| [Analytics](../Analytics/analytics-layer.md) | Segment performance (open rate, click rate by segment) reported in analytics layer |
| [Queue / Pub-Sub](../Platform/queue-and-pubsub.md) | `segment.contact_entered` and `segment.contact_exited` events published to NATS |
