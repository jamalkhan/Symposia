# Use Case: Cart Abandon (E2E)

**Status: Specced (MVP P0)**  
**Template key:** `cart_abandon` (see [Journey template library](../Journeys/journeys.md#platform-template-library))

## Goal

Recover revenue when a contact **adds items to cart** (or maintains cart state) and **leaves without purchasing**. The platform detects session end with cart activity and no purchase, then runs a short email recovery sequence.

This is the **MVP flagship automation**: tracker + [session model](../Tracking/session-model.md) + [Campaign](../Messaging/campaigns.md) (journey-backed) + [email delivery](../Messaging/outbound-email-delivery.md).

---

## Actors & prerequisites

| Actor | Requirement |
|---|---|
| **Marketer** | Tenant with verified **sender profile**, email path (shared pool or Email IP), list/segment of marketable contacts |
| **Site** | Tracker installed; `site_id` resolved; session TTL configured (tenant default and/or site override) |
| **Contact** | Ideally **identified** (`contact_id` via identify / email link). Anonymous carts: hold enrollment until identify **or** skip email (MVP: **require contact_id** to send; optional queue anonymous 24h awaiting identify) |

**Marketer must instrument** (see [Event Schema](../Tracking/event-schema.md)):

- `cart_add` / `cart_remove` / `cart_viewed` (and/or server-side cart sync)  
- `purchase` on success  
- Session activity so TTL can expire  

---

## Detection model (authoritative)

**Primary trigger is not a pure “1 hour after add_to_cart” timer.**  
MVP primary path uses **`session_expired`** with cart context (aligns session TTL with abandon intent and multi-tab behavior).

### Primary: `session_expired` cart abandon

```
session open
  → cart activity (had_cart_activity)
  → no purchase in session (had_purchase = false)
  → inactivity ≥ site/tenant session TTL
  → session_expired emitted
  → Campaign trigger matches filters
  → enroll Journey-backed Campaign
```

**NATS:** `sym.{tenant_id}.events.web.session_expired`

**Trigger filter (Campaign trigger_config):**

```json
{
  "event_type": "session_expired",
  "all": [
    { "field": "properties.had_cart_activity", "op": "eq", "value": true },
    { "field": "properties.had_purchase", "op": "eq", "value": false },
    { "field": "contact_id", "op": "is_not_null" },
    { "field": "site_id", "op": "eq", "value": "{{ campaign.site_id }}" }
  ]
}
```

Optional: `cart_item_count >= 1`, `cart_value >= minimum`.

### Secondary (optional template toggle): classic absence wait

Some marketers still want “1 hour after last `cart_add` even if session still open” (long checkout tabs with heartbeat). Template option:

1. Trigger: `cart_add` (identified contact)  
2. Wait up to `T` (default **60 minutes**) with exit on `purchase` for same `cart_id` / contact  
3. On timeout → same email path as below  

**MVP default template uses session_expired.** Absence wait is an alternate Journey graph in the same template pack.

---

## Why session_expired (not only add_to_cart wait)

| Concern | Session-based |
|---|---|
| User still browsing after add | Heartbeat/activity extends session; abandon waits until they leave |
| Multi-tab | One session_id; single expire |
| Aligns with browse abandon | Same event, different filters |
| Marketer control | Session TTL is tenant/site config |

Cart context on expire must include **latest cart snapshot** (items, total, URL) from session state service — not only first add_to_cart payload.

---

## Eligibility (pre-enroll)

Before creating enrollment, all must pass:

| Check | Fail action |
|---|---|
| `contact_id` present | Skip (or pending-identify queue) |
| `email_status = subscribed` (marketing) | Skip |
| Not on suppression list | Skip |
| Consent / pre-send would allow marketing email | Skip |
| Re-entry policy allows | Skip with `journey_reentry_blocked` |
| Frequency cap (marketing priority) | Delay or skip per campaign `on_frequency_cap` |
| Quiet hours | Delay send (enrollment may proceed to wait) |
| `site_id` matches campaign binding | Skip |

---

## Campaign + Journey shape (default template)

**Campaign**

| Field | Value |
|---|---|
| `campaign_type` | `triggered` |
| `execution_mode` | `journey_backed` |
| `category` | `marketing` |
| `priority` | `marketing` |
| `re_entry_policy` | `re_entry_after_cooldown` |
| `re_entry_cooldown_days` | **7** (template default; marketer editable 1–90) |
| `sender_profile_id` | Marketer-selected |

**Journey graph**

```
[Trigger: session_expired + cart filters]
        │
        ▼
[Wait: fixed 0–15 min optional "cooling" delay]  ── exit if purchase
        │
        ▼
[Action: Send email 1 — "You left something behind"]
        │  context: journey.event_data cart lines, cart_url, images
        ▼
[Wait: 24 hours]  ── exit if purchase
        │
        ▼
[Branch: has purchase event since enrollment?]
    │ yes                          │ no
    ▼                              ▼
[Exit completed]         [Action: Send email 2 — "Last chance"]
                                   │
                                   ▼
                            [Exit completed]
```

**Global exit conditions:** unsubscribed; contact deleted; `purchase` for same contact (and optional same `cart_id`).

**Email 2 optional:** template flag `include_second_email` default **true**.

---

## Personalization context

Enrollment `context` (from `session_expired.properties` + session cart store):

```json
{
  "trigger_event": "session_expired",
  "site_id": "uuid",
  "session_id": "uuid",
  "event_data": {
    "cart_id": "cart_abc",
    "cart_url": "https://shop.example.com/cart",
    "cart_total": 129.99,
    "currency": "USD",
    "item_count": 2,
    "items": [
      {
        "product_id": "sku_99",
        "name": "Trail Running Shoes",
        "quantity": 1,
        "price": 129.99,
        "image_url": "https://cdn.example.com/sku_99.jpg",
        "url": "https://shop.example.com/products/trail-shoes"
      }
    ]
  }
}
```

Liquid examples:

```liquid
{{ journey.event_data.items[0].name }}
{{ journey.event_data.cart_total | currency: "USD" }}
{{ journey.event_data.cart_url }}
```

If cart lines missing (instrumentation gap), still send generic abandon copy; log `context_incomplete` for marketer diagnostics.

---

## Identity: anonymous vs identified

| Case | MVP behavior |
|---|---|
| Identified before expire | Full path |
| Anonymous expire with cart | Emit `session_expired`; **do not send email**. Optionally store `pending_abandon` keyed by `brand_visitor_id` for **24h**; if `identify` fires with cart still recoverable, enroll then |
| Identify after abandon email | N/A |
| Purchase as guest then account | `purchase` event should carry email; exit active abandon if contact matches |

---

## Re-entry, caps, multi-site

| Policy | Default |
|---|---|
| Re-entry cooldown | 7 days from last enrollment start |
| Cross-campaign frequency | Platform 2/day, 7/week (marketing) |
| Site scope | One Campaign per `site_id` recommended; multi-site tenants clone template per site |
| Concurrent abandon enrollments | Blocked by re-entry while active |

---

## Events written (audit / analytics)

| Event | When |
|---|---|
| `session_expired` | Detection |
| `trigger_matched` | Filter pass |
| `campaign_enrolled` / `journey_enrolled` | Enroll |
| `journey_step_*` | Graph progress |
| `email_sent` / open / click | Delivery |
| `journey_exited` | Complete / purchase / unsub |
| `campaign_send_skipped` | Cap / compliance |

Analytics: [Journey performance](../Analytics/analytics-layer.md#6-journey-performance) + campaign performance; optional recovery revenue if purchase within attribution window after click.

---

## Marketer setup checklist

1. Tracker on storefront with cart + purchase events  
2. Session settings (TTL) — often **30–60 min** on shop site  
3. Sender profile + domain  
4. Clone `cart_abandon` template → bind site, sender, lists/eligibility segment  
5. Edit email copy / Liquid  
6. Activate Campaign  
7. Test: add to cart as identified test contact → wait TTL (or force expire in staging) → receive email 1  

---

## Staging / test hooks

| Hook | Purpose |
|---|---|
| `POST /marketing/tracking/sessions/{id}/expire` (tenant admin, non-prod or feature-flagged) | Force `session_expired` without waiting |
| Dry-run enroll | Preview eligibility without send |
| Seed list | Always receive abandon emails when campaign sends |

---

## Failure modes

| Failure | Handling |
|---|---|
| Session sweeper lag | Document max delay ≈ sweeper cadence (≤1 min) + TTL |
| Missing cart snapshot | Generic email; instrumentation health alert |
| Purchase after email 1 queued | Pre-send purchase check if possible; Journey exit cancels later steps |
| Shared IP / abuse pause | Abandon sends pause with other marketing |

---

## Non-goals (this use case)

- SMS abandon  
- Cross-device cart merge beyond existing identity link  
- On-site popup (marketer’s storefront)  
- Browse abandon (separate use case; same `session_expired`, different filters)  

---

## Implementation map

| Component | Responsibility |
|---|---|
| JS tracker | Cart events, session id, activity |
| Session service | TTL, snapshot cart, emit `session_expired` |
| Campaign trigger evaluator | Filter + re-entry + enroll |
| Journey executor | Waits, branch, send actions |
| Personalization | Render cart context |
| Email IP / shared pool | Deliver |
| Pre-send compliance | Suppression, consent, caps |

---

## References

- [session-model.md](../Tracking/session-model.md)  
- [event-schema.md](../Tracking/event-schema.md)  
- [journeys.md](../Journeys/journeys.md)  
- [campaigns.md](../Messaging/campaigns.md)  
- [personalization-engine.md](../Messaging/personalization-engine.md)  
- [MVP.md](../MVP.md)  
