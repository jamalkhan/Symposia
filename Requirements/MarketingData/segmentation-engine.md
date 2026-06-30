# Segmentation Engine

## Overview

The segmentation engine lets marketers define audiences — filtered subsets of their contact database — based on contact properties, activity, list membership, and compliance status. Segments are used to target campaigns, automate triggers, and build audiences for ad platforms.

The distinction between **lists** and **segments** is important:
- A **list** is a static collection. Contacts are explicitly added and removed.
- A **segment** is a dynamic filter. Membership is computed at query time based on current contact data.

Both can be used as a campaign target. Lists are faster (a simple join). Segments are more powerful but add query time at send.

---

## Segment Definition

A segment is defined by a **filter tree** — a structured set of conditions combined with AND / OR / NOT logic.

### Filter Types

| Category | Filter | Examples |
|---|---|---|
| **Contact field** | Equals, not equals, contains, starts with, is empty, is not empty | `email contains "@gmail.com"`, `country = "US"` |
| **Custom property** | Equals, not equals, greater than, less than, between, is set, is not set | `properties.ltv > 500`, `properties.plan_tier = "Pro"` |
| **List membership** | Is in list, is not in list | `is in list "VIP Customers"` |
| **Email activity** | Has received, has opened, has clicked, has not opened (since date) | `opened any email in last 30 days` |
| **Subscription status** | Is subscribed, is unsubscribed, has bounced | `email_status = subscribed` |
| **Compliance** | Has consent, consent expires within | `implied_consent_expires_at < now + 30 days` |
| **Date** | Created before/after, last activity before/after | `created_at > 2026-01-01` |
| **Tag** | Has tag, does not have tag | `has tag "re-engagement"` |
| **Symposia identity** | Has linked Symposia profile, does not have | `symposia_identity_id is not null` |

### Filter Tree Structure (JSON)

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
    }
  ]
}
```

### Translates to SQL

The segment engine compiles filter trees to parameterized SQL queries against `marketing.contacts` (joined to `marketing.contact_events` and `marketing.list_memberships` where needed).

The above example compiles to:
```sql
SELECT contact_id FROM marketing.contacts
WHERE tenant_id = $1
  AND email_status = 'subscribed'
  AND country IN ('US', 'CA')
  AND (
    properties->>'plan_tier' = 'Pro'
    OR (properties->>'ltv')::numeric > 500
  )
```

---

## Segment API

```
GET    /marketing/segments                    List segments
POST   /marketing/segments                    Create segment
GET    /marketing/segments/{id}               Get segment definition
PUT    /marketing/segments/{id}               Update definition
DELETE /marketing/segments/{id}               Delete segment
GET    /marketing/segments/{id}/count         Count matching contacts (real-time estimate)
GET    /marketing/segments/{id}/preview       Sample 10 contacts matching segment
POST   /marketing/segments/{id}/export        Export matching contacts
POST   /marketing/segments/{id}/sync-list     Materialize segment into a static list
```

### Count Endpoint

Before sending a campaign, marketers need to know how many people are in the segment. The count endpoint runs the compiled SQL query with `COUNT(*)` and returns the result. For large databases, an approximate count (via `TABLESAMPLE` or statistics) may be returned for initial display, with an exact count computed in the background.

---

## Segment-Based Campaign Targeting

When a campaign is targeted at a segment (rather than a list), the delivery pipeline:

1. At job creation, computes the full segment query and snapshots the contact IDs into the delivery queue. This snapshot is taken at job start, not at send time — the segment is not re-evaluated per-message.
2. The pre-send compliance check still runs per recipient (suppression, unsubscribe status can change between snapshot and send).
3. The snapshot approach ensures consistent targeting: contacts who join the segment after the campaign starts do not receive the email.

---

## Open Questions

- **Behavior-based segments** (e.g., "visited pricing page 3+ times in last 7 days"): These require joining against web tracking events, not just email events. This crosses into the tracking system and the analytics layer. Should the segmentation engine support this at launch, or only in a phase 2 that includes the tracking pipeline?
- **Segment size limits**: Very large segments (>1M contacts) can be expensive to evaluate and snapshot. Should there be a size cap with a "submit for async processing" pattern?
- **Suppression segments**: A segment used as a suppression target (send to everyone in list EXCEPT members of this segment) is a useful feature. Does the filter tree need an explicit exclude-if-in-segment option?
