# Analytics Layer

## Overview

The analytics layer provides marketers with insight into campaign performance, contact engagement, list health, journey effectiveness, and revenue attribution. It is built on **DuckDB** — an embedded, in-process analytical engine — querying the platform's existing blob storage (Parquet) and Postgres layers. No additional server infrastructure is required.

v1 exposes **report-style endpoints only** (predefined reports, structured JSON responses). A future phase will add ad-hoc SQL / segment explorer capability once the report layer is stable and the query isolation model is validated.

---

## Technology: DuckDB

DuckDB runs embedded inside the Analytics API service process. It has no separate server, no cluster to operate, and reads data from two sources:

1. **Postgres** (`marketing.contact_events`) — hot data, last 30 days. DuckDB's native Postgres scanner reads directly from the contact events table.
2. **Blob storage (Parquet)** — cold data, 30 days → 7 years. DuckDB's S3 extension reads Parquet files directly from the tenant's blob prefix, with no intermediate ETL step.

DuckDB `UNION ALL`s these two sources transparently at query time, so report queries span any date range without the caller needing to know where the data lives.

```
Postgres (hot, 0–30 days)  ──┐
                              ├──▶  DuckDB (embedded, Analytics API)  ──▶  Report Endpoints
Parquet / Blob (cold, 30d+) ──┘
```

### Why Not ClickHouse

ClickHouse is better for sub-second queries over hundreds of billions of rows. At the volume most marketers generate in v1, DuckDB on Parquet is more than sufficient (DuckDB handles tens of billions of rows on modest hardware). ClickHouse stays open as a higher-tier offering for large-volume tenants in a future phase. The interfaces defined here (report endpoints + data model) would be identical — only the query execution engine would differ.

---

## Per-Tenant Analytics Store

Each tenant's analytics data is isolated to their own blob prefix and Postgres schema. The analytics service enforces `tenant_id` scoping at the query level — no cross-tenant data access is possible.

The analytics "store" for a tenant is not a separate database file — it is their existing data, queried through DuckDB:

| Layer | Location | Content |
|---|---|---|
| Hot events | `marketing.contact_events` (Postgres) | All events, last 30 days |
| Cold events | `s3://sym-events/{tenant_id}/{year}/{month}/{day}/{hour}.parquet` | All events, 30 days → 7 years |
| Pre-aggregated summaries | Analytics node local disk (rebuilt from Parquet on TTL) | Daily rollup tables for fast report queries |

The pre-aggregated summary tables are the analytics service's internal cache — they are not exposed externally. When a report query arrives, the analytics service checks whether the summary tables are current; if not, it runs a background Parquet scan to rebuild them before responding. Staleness tolerance is configurable per report type (default: summary tables rebuilt daily at 2 AM tenant-local time).

### Summary Tables (Internal)

These tables are maintained by the analytics service and stored locally on the analytics node:

| Table | Granularity | Columns |
|---|---|---|
| `daily_email_stats` | Per campaign × per day | `campaign_id`, `date`, `sent`, `delivered`, `unique_opens`, `total_opens`, `unique_clicks`, `total_clicks`, `hard_bounces`, `soft_bounces`, `complaints`, `unsubscribes` |
| `daily_contact_stats` | Per day | `date`, `new_contacts`, `contacts_unsubscribed`, `contacts_bounced`, `contacts_deleted`, `net_growth` |
| `contact_engagement_snapshot` | Per contact (weekly rebuild) | `contact_id`, `last_open_at`, `last_click_at`, `last_any_activity_at`, `engagement_tier` |
| `daily_journey_stats` | Per journey × per day | `journey_id`, `date`, `enrollments`, `completions`, `early_exits`, `active` |
| `daily_revenue_attribution` | Per campaign × per day | `campaign_id`, `date`, `attributed_orders`, `attributed_revenue`, `attribution_model` |

---

## Parquet Schema

Blob archives are written in **Parquet format** with **Zstd compression**. See [Event Integrity — Blob Storage Layout](../Platform/event-integrity.md#blob-storage-layout) for path structure.

Each event is one row. High-cardinality analytics fields are flattened to top-level columns at write time (the archiver extracts them from `properties` when writing Parquet). The remainder of `properties` is serialized as a JSON string column for flexibility.

```
event_id              STRING     NOT NULL   -- UUID v7
tenant_id             STRING     NOT NULL
event_type            STRING     NOT NULL
occurred_at           TIMESTAMP  NOT NULL   -- partition key
source                STRING               -- web | email | api | import
contact_id            STRING               -- null until identified
brand_visitor_id      STRING
network_visitor_id    STRING
symposia_identity_id  STRING
session_id            STRING

-- Flattened from properties (email events)
campaign_id           STRING
send_id               STRING

-- Flattened from properties (journey events)
journey_id            STRING
enrollment_id         STRING

-- Flattened from properties (e-commerce events)
order_id              STRING
order_revenue         DOUBLE

-- Server-enriched
geo_country           STRING
geo_region            STRING
device_type           STRING               -- desktop | mobile | tablet | bot

-- Remaining properties
properties            STRING               -- JSON; not queried in summary paths
```

Flattening `campaign_id`, `send_id`, `journey_id`, and `order_revenue` into top-level columns means common analytics queries (group by campaign, sum revenue) run as column scans — no JSON parsing in the hot path.

---

## Report Endpoints (v1)

All endpoints are scoped to the authenticated marketer's tenant. All support `start_date` / `end_date` query parameters (ISO 8601 dates). Default date range: last 30 days.

### 1. Campaign Performance

```
GET /analytics/campaigns/{campaign_id}/performance
    ?start_date=2026-06-01&end_date=2026-06-30
    &group_by=day            // day | week | month | total (default: total)
```

Response:
```json
{
  "campaign_id": "uuid",
  "campaign_name": "June Newsletter",
  "date_range": { "start": "2026-06-01", "end": "2026-06-30" },
  "totals": {
    "sent": 124500,
    "delivered": 121800,
    "delivery_rate": 0.978,
    "unique_opens": 38700,
    "open_rate": 0.318,
    "unique_clicks": 9200,
    "click_rate": 0.076,
    "click_to_open_rate": 0.238,
    "hard_bounces": 1100,
    "soft_bounces": 1600,
    "bounce_rate": 0.022,
    "complaints": 12,
    "complaint_rate": 0.0001,
    "unsubscribes": 340,
    "unsubscribe_rate": 0.0028
  },
  "series": [
    { "date": "2026-06-01", "sent": 124500, "delivered": 121800, ... }
  ]
}
```

`series` is only included when `group_by` is not `total`.

---

### 2. Campaign List Summary

```
GET /analytics/campaigns
    ?start_date=2026-06-01&end_date=2026-06-30
    &sort_by=sent            // sent | open_rate | click_rate | complaints (default: sent desc)
    &limit=50&offset=0
```

Returns a list of campaigns with top-line stats (same fields as `/performance` totals, no series). Useful for the campaign overview dashboard.

---

### 3. Email Deliverability Health

```
GET /analytics/email/deliverability
    ?start_date=2026-06-01&end_date=2026-06-30
    &group_by=week
```

```json
{
  "date_range": { "start": "2026-06-01", "end": "2026-06-30" },
  "sending_ip_type": "dedicated",
  "health_summary": {
    "bounce_rate": 0.018,
    "complaint_rate": 0.00009,
    "status": "healthy"    // healthy | warning | critical
  },
  "thresholds": {
    "bounce_rate_warning": 0.02,
    "bounce_rate_critical": 0.05,
    "complaint_rate_warning": 0.0008,
    "complaint_rate_critical": 0.001
  },
  "series": [
    { "week": "2026-06-01", "bounce_rate": 0.019, "complaint_rate": 0.00011, "volume": 42000 }
  ]
}
```

The `status` field gives the marketer a simple traffic-light deliverability health indicator. `warning` means "approaching shared pool thresholds or ISP feedback loop risk." `critical` means "sends may be blocked or suppressed."

---

### 4. Contact Engagement Distribution

```
GET /analytics/contacts/engagement
    ?as_of=2026-06-30       // snapshot date (default: today)
```

```json
{
  "as_of": "2026-06-30",
  "total_subscribed": 84000,
  "distribution": {
    "highly_engaged":  { "count": 12400, "pct": 0.148, "definition": "opened or clicked in last 90 days" },
    "engaged":         { "count": 21000, "pct": 0.250, "definition": "opened or clicked in last 180 days" },
    "at_risk":         { "count": 29800, "pct": 0.355, "definition": "opened or clicked in last 12 months" },
    "lapsed":          { "count": 14200, "pct": 0.169, "definition": "no open or click in 12+ months" },
    "never_engaged":   { "count":  6600, "pct": 0.079, "definition": "no open or click ever recorded" }
  },
  "recommendation": "Consider a re-engagement journey for your 14,200 lapsed contacts before suppressing them."
}
```

Tiers are based on the `contact_engagement_snapshot` summary table (weekly rebuild). The `recommendation` is a platform-generated hint — always optional copy, never a blocking action.

---

### 5. Audience Growth

```
GET /analytics/contacts/growth
    ?start_date=2026-01-01&end_date=2026-06-30
    &group_by=month
```

```json
{
  "date_range": { "start": "2026-01-01", "end": "2026-06-30" },
  "totals": {
    "starting_count": 68000,
    "ending_count": 84000,
    "net_growth": 16000,
    "new_contacts": 22400,
    "contacts_removed": 6400
  },
  "series": [
    {
      "month": "2026-06-01",
      "new_contacts": 4200,
      "unsubscribed": 380,
      "bounced": 210,
      "deleted": 45,
      "net": 3565
    }
  ]
}
```

---

### 6. Journey Performance

```
GET /analytics/journeys/{journey_id}/performance
    ?start_date=2026-06-01&end_date=2026-06-30
```

```json
{
  "journey_id": "uuid",
  "journey_name": "Welcome Series",
  "date_range": { "start": "2026-06-01", "end": "2026-06-30" },
  "summary": {
    "total_enrollments": 4200,
    "active_enrollments": 1800,
    "completed": 1900,
    "early_exits": 500,
    "completion_rate": 0.792
  },
  "steps": [
    {
      "step_id": "uuid",
      "step_name": "Welcome Email",
      "step_type": "action",
      "entries": 4200,
      "exits": 120,
      "exit_reasons": { "unsubscribed": 80, "global_opt_out": 40 },
      "completion_pct": 0.971,
      "email_performance": {
        "sent": 4080,
        "open_rate": 0.42,
        "click_rate": 0.18
      }
    }
  ],
  "revenue_attributed": {
    "orders": 312,
    "revenue": 28940.00,
    "currency": "USD",
    "attribution_model": "last_touch_7d"
  }
}
```

Step-level funnel shows where contacts are dropping off. `revenue_attributed` is only present if the tenant has purchase events wired up.

---

### 7. Revenue Attribution

```
GET /analytics/revenue/attribution
    ?start_date=2026-06-01&end_date=2026-06-30
    &group_by=campaign         // campaign | journey | month
```

```json
{
  "date_range": { "start": "2026-06-01", "end": "2026-06-30" },
  "attribution_model": "last_touch_7d",
  "totals": {
    "attributed_orders": 1840,
    "attributed_revenue": 184230.00,
    "currency": "USD",
    "revenue_per_email_sent": 0.72
  },
  "breakdown": [
    {
      "campaign_id": "uuid",
      "campaign_name": "June Newsletter",
      "attributed_orders": 420,
      "attributed_revenue": 39800.00,
      "sends": 124500,
      "revenue_per_send": 0.32
    }
  ]
}
```

**Attribution model**: last-touch email click before a `purchase` event. The lookback window is **marketer-configurable** (1–90 days, default 7 days). The selected window is stored in the tenant's analytics settings. Longer windows increase the historical Parquet scan range and thus the compute cost of attribution queries — this is reflected in the tenant's analytics compute billing. Multi-touch attribution (linear, time-decay, data-driven) is a future phase.

---

### 8. Compliance / Opt-Out Report

```
GET /analytics/compliance/opt-outs
    ?start_date=2026-06-01&end_date=2026-06-30
    &group_by=week
```

```json
{
  "date_range": { "start": "2026-06-01", "end": "2026-06-30" },
  "totals": {
    "unsubscribes": 1240,
    "unsubscribe_rate_of_sends": 0.0028,
    "by_mechanism": {
      "one_click_header": 680,
      "preference_center": 420,
      "manual": 140
    },
    "deletion_requests": 18,
    "global_opt_outs": 6
  },
  "series": [
    { "week": "2026-06-01", "unsubscribes": 310, "deletion_requests": 4 }
  ]
}
```

---

## Data Freshness

Analytics reports do not need to be real-time. Acceptable latency is **30 minutes to 2 hours maximum**. Sub-minute live dashboards (e.g., watching open rates tick up during an active send) are **not a v1 requirement** and will only be built as a by-request feature if specific clients require it — it requires a separate streaming aggregation path (NATS → rolling aggregate → WebSocket push) that is out of scope here.

| Report | Data Source | Maximum Staleness |
|---|---|---|
| Campaign performance (last 30d) | Postgres + summary tables | 30 minutes |
| Campaign performance (30d+) | Parquet + summary tables | 2 hours |
| Deliverability health | Postgres + summary tables | 1 hour |
| Engagement distribution | `contact_engagement_snapshot` | 2 hours (weekly rebuild) |
| Audience growth | Summary tables | 1 hour |
| Journey performance | Postgres + summary tables | 1 hour |
| Revenue attribution | Summary tables | 2 hours |

All report responses include a `data_current_as_of` timestamp so the caller knows the freshness of the data they received.

Summary tables are rebuilt by a background job that processes new Parquet files as they arrive from the integrity archiver. The target rebuild cadence is every 30 minutes; the 2-hour maximum is the SLA for the job under high node load.

---

## Future Phase: Ad-Hoc SQL / Segment Explorer

In a subsequent phase, the analytics layer will expose raw DuckDB query capability for:
- **Segment explorer**: build arbitrary contact segments with SQL-like conditions across event history and contact properties
- **Ad-hoc queries**: marketers submit SQL against their event data and receive results (async job model for large queries)
- **Exports**: query results as CSV or Parquet download

This requires additional work on query isolation (preventing expensive queries from consuming all analytics node compute), query cost estimation (reject or rate-limit queries that would scan excessive data), and result caching. The report endpoints defined in v1 are designed to validate the underlying data model before this surface is opened up.

---

## Query Execution Model

### Synchronous vs. Async

Report API responses are **synchronous up to 30 seconds**. If the analytics service estimates that a query will exceed 30 seconds of execution time (based on projected scan size — e.g., >500M rows, or a full-history date range on a large tenant), it rejects the synchronous request and returns a job reference instead:

```
HTTP 202 Accepted
{
  "job_id": "uuid",
  "estimated_duration_seconds": 90,
  "status_url": "/analytics/queries/{job_id}"
}
```

The client polls `/analytics/queries/{job_id}` until `status` is `complete`, then fetches results from `/analytics/queries/{job_id}/results`.

All 8 standard report endpoints support both sync and async paths. The analytics service selects the path automatically — the caller does not choose.

```
GET  /analytics/queries/{job_id}          Poll for status
GET  /analytics/queries/{job_id}/results  Fetch completed results (JSON or Parquet download)
DELETE /analytics/queries/{job_id}        Cancel a pending job
```

### Analytics Node

The analytics service runs on **dedicated Analytics nodes** — a distinct node type from OLTP and Storage. Analytics nodes are optimized for high-RAM, high-CPU workloads. Node operators self-select this type at container configuration time. See [Node Types and Dynamic Rewards](../Platform/node-types-and-rewards.md) for resource requirements, mining reward mechanics, and the dynamic incentive system that governs network composition.

## Answered Questions

1. **Analytics node topology**: Dedicated Analytics node type. High-RAM, high-CPU Docker containers, selected by node operators at configuration time. Resource guarantees are binding — misses reduce mining yield. See [Node Types and Dynamic Rewards](../Platform/node-types-and-rewards.md).

2. **Query timeout and cost limits**: Synchronous ≤30 seconds; async job model for larger queries. Analytics service performs cost estimation before execution and forces async if projected scan exceeds threshold.

3. **Real-time vs. daily fresh**: Not needed for v1. 30-minute to 2-hour latency is acceptable. Sub-minute live dashboards are a by-request future feature requiring a separate streaming aggregation path.

4. **Attribution lookback window**: Marketer-configurable (1–90 days, default 7 days). Longer windows increase compute cost of attribution queries, reflected in analytics billing.
