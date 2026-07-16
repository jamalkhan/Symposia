# Session Model

## Overview

A **session** groups a visitor’s continuous activity on a marketer property (site/app). Sessions power analytics, personalization context, and **abandon automations** (cart abandon, browse abandon): when a session ends without conversion, the platform emits a `session_expired` event that Triggered Campaigns / Journeys can use.

This document defines session identity, inactivity timeout, expiration events, and **who configures timeout** (marketers — not a fixed platform global).

Related: [Tracking Architecture](./tracking-architecture.md), [Event Schema](./event-schema.md), [Campaigns](../Messaging/campaigns.md), abandon use cases under `Requirements/UseCases/`.

---

## Session Scope: Tenant vs Site

Marketers configure session behavior at two levels:

| Level | What it is | Configures |
|---|---|---|
| **Tenant** | The marketer account (one Symposia tenant) | **Default** session settings for all sites that do not override |
| **Site** (tracked property) | A distinct web/app property under the tenant (e.g. `malamute.com`, `shop.malamute.com`, iOS app) | **Override** session settings for that property only |

**Resolved product rule:** session expiration (inactivity TTL and related options) is **defined by the marketer** at **tenant level and/or site level**. The platform does not impose a single network-wide timeout that all brands must share.

### Site identity

A **site** is a first-class (lightweight) config object under the tenant:

| Field | Description |
|---|---|
| `site_id` | Platform UUID |
| `tenant_id` | Owning marketer |
| `name` | Display name (e.g. "Malamute Storefront") |
| `domains[]` | Registrable domains / hosts that map to this site (e.g. `malamute.com`, `www.malamute.com`) |
| `tracker_keys` | Optional public key / write key used in `_sym('init', …)` |
| `session_config` | Optional override of tenant session defaults (see below) |

Tracker init must identify the site:

```javascript
_sym('init', {
  tenant_id: 'tenant_01abc',
  site_id: 'site_01xyz',           // preferred
  brand_domain: 'malamute.com',    // used to resolve site_id if site_id omitted
  // ...
});
```

Resolution order for `site_id` on an event:

1. Explicit `site_id` in tracker init / event payload  
2. Match `brand_domain` / request host against tenant `sites.domains[]`  
3. If still unresolved: attach to tenant **default site** (auto-created at tenant onboarding) and log a config warning  

All session state and `session_expired` events are **scoped to `(tenant_id, site_id, session_id)`** — a session on site A never expires site B’s session for the same visitor.

---

## Session Definition

| Concept | Definition |
|---|---|
| **session_id** | UUID assigned when a new session starts; present on subsequent events in that session (see [Event Schema](./event-schema.md) common envelope) |
| **Start** | First trackable event after no active session (page view, identify, ecom event, etc.), subject to consent |
| **Continue** | Each subsequent event for the same `brand_visitor_id` (or identified `contact_id`) on the **same site** within the inactivity window refreshes `last_activity_at` |
| **Expire** | No qualifying activity for the configured **inactivity TTL** on that site → session closed → `session_expired` emitted |

Default inactivity window if the marketer never configures anything: **30 minutes** (industry-common default). Marketers may set any value in the allowed range (see [Configuration](#configuration)).

Cross-tab / multi-device: same browser profile with the same `brand_visitor_id` on the same site shares one session; different devices are different sessions unless linked by identity after the fact (events remain on their original `session_id`).

---

## Configuration

### Tenant-level defaults

```
GET/PUT /marketing/tracking/session-settings
```

```json
{
  "tenant_id": "uuid",
  "inactivity_ttl_seconds": 1800,
  "min_ttl_seconds": 300,
  "max_ttl_seconds": 86400,
  "emit_session_expired": true,
  "session_expired_include_context": true,
  "heartbeat_seconds": 60,
  "updated_at": "2026-07-15T00:00:00Z"
}
```

| Field | Meaning |
|---|---|
| `inactivity_ttl_seconds` | Seconds without activity before expire (default **1800** = 30 min) |
| Allowed range | Platform enforces **300 (5 min) … 86400 (24 h)** unless governance changes bounds |
| `emit_session_expired` | If false, sessions still close internally for analytics sessionization but **no** `session_expired` event is published (abandon Journeys will not fire) |
| `session_expired_include_context` | If true, expiration payload includes cart/browse summary snapshot (see event schema) |
| `heartbeat_seconds` | Optional client heartbeat interval while tab is open (keeps session alive during long reads); 0 = disabled |

### Site-level overrides

```
GET/PUT /marketing/tracking/sites/{site_id}/session-settings
```

Same shape as tenant settings. **Null / omitted fields inherit tenant defaults.**

Effective config for a session:

```
effective = tenant_defaults  overlay  site.session_config
```

Example: tenant default 30 min; checkout subdomain site sets 60 min so long checkout flows do not expire mid-funnel; blog site keeps 15 min for tighter abandon timing.

### Who can edit

Marketer roles with tracking/automation admin permission (tenant RBAC — when RBAC is fully specced). Not configurable by end individuals. Not a governance/chain parameter.

---

## Expiration Mechanics

1. Server (or edge session service) maintains `last_activity_at` per `(tenant_id, site_id, session_id)`.
2. Activity = ingested events that count as engagement (page_view, scroll, click, cart_*, identify, custom with `extends_session: true`, heartbeats if enabled). Bot-classified traffic may be excluded per tenant bot policy.
3. When `now - last_activity_at >= effective_ttl` and session status is `open`, mark session `expired` and emit **one** `session_expired` event (idempotent on `session_id`).
4. Implementation may use lazy expiry (next event sees timeout) **plus** a sweeper for abandon (must emit even if the user never returns). Sweeper cadence ≤ 1 minute for TTLs ≥ 5 minutes.
5. A new session starts on the next activity after expiry (new `session_id`).

---

## `session_expired` Event

Emitted to NATS and written to `marketing.contact_events` when `contact_id` is known; anonymous sessions still emit with `brand_visitor_id` / `network_visitor_id` for later identity stitch.

```json
{
  "event_type": "session_expired",
  "source": "web",
  "tenant_id": "uuid",
  "site_id": "uuid",
  "session_id": "uuid",
  "brand_visitor_id": "uuid",
  "contact_id": "uuid-or-null",
  "occurred_at": "2026-07-15T14:30:00Z",
  "properties": {
    "inactivity_ttl_seconds": 1800,
    "session_started_at": "2026-07-15T14:00:00Z",
    "last_activity_at": "2026-07-15T14:00:00Z",
    "pageview_count": 12,
    "had_cart_activity": true,
    "had_purchase": false,
    "had_product_view": true,
    "product_view_count": 3,
    "cart_id": "cart-uuid",
    "cart_item_count": 2,
    "cart_value": 129.99,
    "currency": "USD",
    "last_product_id": "sku-99",
    "last_product_url": "https://malamute.com/products/trail-shoes",
    "last_category_id": "uuid",
    "config_source": "site" 
  }
}
```

`config_source`: `tenant` | `site` — which level supplied the effective TTL (for debugging).

### Downstream use (abandon)

| Signal on `session_expired` | Typical automation |
|---|---|
| `had_cart_activity && !had_purchase` | [Cart abandon](../UseCases/cart-abandon.md) |
| `had_product_view && !had_cart_activity && !had_purchase` | [Browse abandon](../UseCases/browse-abandon.md) |
| `had_purchase` | Usually no abandon enroll (filter on trigger) |

Session expire payload should include cart fields **and** browse fields (`had_product_view`, `last_product_*`, `product_view_count`, etc.) as specified in those use cases.

Exact Journey graphs subscribe to **`session_expired`**, not only hard-coded add_to_cart / pageview timers.

NATS subject: `sym.{tenant_id}.events.web.session_expired` (or `sym.{tenant_id}.events.session.expired` — pick one in implementation; prefer `events.web.session_expired` for web sessions).

---

## Multi-Site Tenants

- One tenant, many sites: each site has independent sessions and optional independent TTLs.
- Abandon Campaigns should filter by `site_id` when the marketer only wants storefront abandons, not blog sessions.
- Product interest / back-in-stock is site-agnostic unless bound to a site catalog; session abandon is always site-scoped.

---

## Privacy and Consent

- Session tracking requires the same consent categories as brand analytics/marketing tracking ([Tracking Architecture — Consent](./tracking-architecture.md#consent-integration)).
- If consent is declined, do not create sessions or emit `session_expired`.
- Session payloads must not include raw PII beyond what other web events already allow; cart line items follow ecom event rules.

---

## API Summary

```
# Tenant defaults
GET  /marketing/tracking/session-settings
PUT  /marketing/tracking/session-settings

# Sites
GET  /marketing/tracking/sites
POST /marketing/tracking/sites
GET  /marketing/tracking/sites/{site_id}
PATCH /marketing/tracking/sites/{site_id}
GET  /marketing/tracking/sites/{site_id}/session-settings
PUT  /marketing/tracking/sites/{site_id}/session-settings
```

---

## Defaults (summary)

| Setting | Default |
|---|---|
| Config authority | **Marketer** at **tenant** and **site** |
| Tenant inactivity TTL | 30 minutes |
| Site override | Optional; inherits tenant |
| Platform bounds | 5 minutes … 24 hours |
| `session_expired` emission | On (tenant default) |
| Platform-enforced single global TTL | **No** |

---

## Open Questions

1. **App sessions** (iOS/Android): same site object with `platform: app`, or separate app_id? (Lean: same site model, `channel: web \| app`.)
2. **Heartbeat battery/cost**: default heartbeat off on mobile web?  
3. **Timezone of sweeper**: always UTC clocks; TTL is duration-based, not clock-hour-based.  
