# Event Schema

## Overview

Events are the atomic records of individual behavior collected by the tracking system. Every action captured — a page view, a click, a purchase — is an event. Events are immutable records written at the time the action occurs.

All events share a common envelope, with an `event_type` field that determines the additional properties the event carries.

---

## Common Event Envelope

Every event includes these fields regardless of type:

```json
{
  "event_id": "uuid",
  "tenant_id": "tenant_01abc",
  "brand_visitor_id": "uuid",         // anonymous visitor ID (always present)
  "contact_id": "uuid",               // null until identified
  "network_visitor_id": "uuid",       // null if network consent not granted
  "symposia_identity_id": "uuid",     // null until identity linked
  "event_type": "page_view",
  "occurred_at": "2026-06-30T12:34:56.789Z",
  "source": "web",                    // web | email | api | import
  "session_id": "uuid",               // groups events into sessions; inactivity TTL is marketer-configured per tenant/site (default 30 min) — see session-model.md
  "site_id": "uuid",                  // tracked property under the tenant

  // Server-side enriched (never stored raw):
  "geo": {
    "country": "US",
    "region": "Colorado",
    "city": "Denver"
  },
  "device": {
    "type": "desktop",               // desktop | mobile | tablet | bot
    "browser": "Chrome",
    "browser_version": "126",
    "os": "macOS",
    "os_version": "15"
  },
  "referrer": {
    "url": "https://google.com/",    // referring URL (redacted to domain for privacy)
    "source": "google",
    "medium": "organic"
  },
  "utm": {
    "source": "newsletter",
    "medium": "email",
    "campaign": "june_2026",
    "term": null,
    "content": null
  },

  "properties": {}                    // event-specific fields (see below)
}
```

---

## Standard Web Events

These events are collected automatically by the JavaScript tracker with no additional configuration.

### `page_view`

Fired on every page load (and on SPA navigation, via the History API listener).

```json
{
  "event_type": "page_view",
  "properties": {
    "url": "https://malamute.com/hiking-boots",
    "path": "/hiking-boots",
    "title": "Hiking Boots — Malamute Adventures",
    "query_string": "?color=brown",
    "hash": null,
    "duration_ms": null              // set on page unload via sendBeacon
  }
}
```

### `page_exit`

Fired on page unload. Carries the duration the page was active.

```json
{
  "event_type": "page_exit",
  "properties": {
    "url": "https://malamute.com/hiking-boots",
    "duration_ms": 45320,
    "scroll_depth_pct": 72           // how far down the page the user scrolled
  }
}
```

### `scroll`

Fired when the user reaches scroll depth milestones: 25%, 50%, 75%, 90%, 100%.

```json
{
  "event_type": "scroll",
  "properties": {
    "depth_pct": 75,
    "url": "https://malamute.com/hiking-boots"
  }
}
```

### `click`

Fired on all clicks on `<a>` and `<button>` elements (can be limited to specific selectors via tracker config).

```json
{
  "event_type": "click",
  "properties": {
    "element_type": "a",
    "text": "Shop Now",
    "href": "https://malamute.com/shop",
    "element_id": "hero-cta",
    "element_class": "btn btn-primary"
  }
}
```

### `form_submit`

Fired when a form is submitted. No field values are captured by default (privacy-safe). The marketer can opt in to capturing specific non-sensitive field values.

```json
{
  "event_type": "form_submit",
  "properties": {
    "form_id": "email-signup-footer",
    "form_action": "/api/subscribe",
    "fields_submitted": ["email", "first_name"]   // field names, not values
  }
}
```

### `identify`

Fired when a visitor becomes identified (submits their email address). This is the event that links a `brand_visitor_id` to a `contact_id`.

```json
{
  "event_type": "identify",
  "properties": {
    "email": "visitor@example.com",
    "source": "checkout_form",
    "contact_properties": {          // optional — update contact record with these values
      "first_name": "Jamal",
      "properties": { "last_order_total": 89.99 }
    }
  }
}
```

The `identify` event triggers retroactive association: all prior events in the current session (and prior sessions with the same `brand_visitor_id`) are linked to the newly identified contact.

---

## Consent Events

Consent events are emitted by the tracker when a cookie consent banner is shown or when the individual records a consent decision. They are compliance events — persisted to the individual's profile and included in the [Merkle commitment pipeline](../Platform/event-integrity.md). See [Consent Persistence](./tracking-architecture.md#consent-persistence) for where these land.

### `cookie_consent_shown`

Emitted when the marketer's cookie consent banner (or CMP) is rendered to the visitor.

```json
{
  "event_type": "cookie_consent_shown",
  "source": "web",
  "properties": {
    "banner_type": "marketer",              // marketer | symposia_fallback
    "cmp_provider": "onetrust",             // onetrust | cookiebot | usercentrics | osano | custom | null
    "banner_version": "2.1.0",             // marketer-supplied version string, for audit purposes
    "categories_presented": ["necessary", "analytics", "marketing"],
    "includes_symposia_disclosure": true    // marketer attests their banner covers Symposia cookies
  }
}
```

### `cookie_consent_recorded`

Emitted when the visitor makes a consent decision in the marketer's banner. This is the event that triggers consent persistence.

```json
{
  "event_type": "cookie_consent_recorded",
  "source": "web",
  "properties": {
    "banner_type": "marketer",
    "cmp_provider": "onetrust",
    "decision": "custom",                   // accept_all | decline_all | custom
    "categories_granted": ["necessary", "analytics"],
    "categories_declined": ["marketing"],
    "symposia_brand_cookie_granted": false, // was _sym_brand consented to?
    "symposia_network_cookie_granted": false, // was _sym_net consented to?
    "banner_version": "2.1.0",
    "recorded_at": "2026-06-30T14:23:00Z"  // explicit timestamp; not relied on from event envelope alone
  }
}
```

### `symposia_platform_cookie_consent_shown`

Emitted when the Symposia fallback banner is rendered — meaning no marketer banner was detected within the timeout window.

```json
{
  "event_type": "symposia_platform_cookie_consent_shown",
  "source": "web",
  "properties": {
    "banner_type": "symposia_fallback",
    "trigger_reason": "no_marketer_banner_detected",
    "timeout_ms": 3000,                     // how long the tracker waited before showing fallback
    "banner_copy_version": "v1.0"           // versioned copy; critical for legal audit
  }
}
```

### `symposia_platform_cookie_consent_recorded`

Emitted when the visitor makes a consent decision in the Symposia fallback banner.

```json
{
  "event_type": "symposia_platform_cookie_consent_recorded",
  "source": "web",
  "properties": {
    "banner_type": "symposia_fallback",
    "decision": "accept_all",               // accept_all | decline_all | custom
    "symposia_brand_cookie_granted": true,
    "symposia_network_cookie_granted": true,
    "banner_copy_version": "v1.0",
    "recorded_at": "2026-06-30T14:23:05Z"
  }
}
```

---

## Email Events

These events are generated by the platform's email sending and tracking infrastructure, not by the JavaScript tracker.

### `email_sent`

```json
{
  "event_type": "email_sent",
  "source": "email",
  "properties": {
    "send_id": "uuid",
    "campaign_id": "uuid",
    "campaign_name": "June Newsletter",
    "subject": "Jamal, your June update is here",
    "sending_domain": "mail.malamute.com"
  }
}
```

### `email_delivered`

```json
{
  "event_type": "email_delivered",
  "source": "email",
  "properties": {
    "send_id": "uuid",
    "campaign_id": "uuid",
    "recipient_mx": "gmail.com"
  }
}
```

### `email_opened`

```json
{
  "event_type": "email_opened",
  "source": "email",
  "properties": {
    "send_id": "uuid",
    "campaign_id": "uuid",
    "open_type": "human",              // human | machine (Apple MPP proxy)
    "email_client": "Gmail",
    "device_type": "mobile"
  }
}
```

### `email_clicked`

```json
{
  "event_type": "email_clicked",
  "source": "email",
  "properties": {
    "send_id": "uuid",
    "campaign_id": "uuid",
    "link_url": "https://malamute.com/hiking-boots",
    "link_text": "Shop Now",
    "link_position": 3                 // ordinal position of link in email body
  }
}
```

### `email_bounced`

```json
{
  "event_type": "email_bounced",
  "source": "email",
  "properties": {
    "send_id": "uuid",
    "campaign_id": "uuid",
    "bounce_type": "hard",             // hard | soft | block
    "smtp_code": 550,
    "smtp_message": "User unknown"
  }
}
```

### `email_complained`

```json
{
  "event_type": "email_complained",
  "source": "email",
  "properties": {
    "send_id": "uuid",
    "campaign_id": "uuid",
    "complaint_source": "fbl",         // fbl | one_click_unsub | manual
    "isp": "gmail.com"
  }
}
```

### `email_unsubscribed`

```json
{
  "event_type": "email_unsubscribed",
  "source": "email",
  "properties": {
    "send_id": "uuid",
    "campaign_id": "uuid",
    "method": "one_click",             // one_click | preference_center | manual
    "category": null                   // null = all; or category name
  }
}
```

---

## E-Commerce Events

These events capture the buying funnel. They are not tracked automatically — the marketer must instrument their checkout and product pages using the tracker API.

### `product_viewed`

```json
{
  "event_type": "product_viewed",
  "properties": {
    "product_id": "sku-12345",
    "product_name": "Trail Blazer Boots",
    "category": "Footwear",
    "brand": "Merrell",
    "price": 149.99,
    "currency": "USD",
    "variant": "Brown / Size 11",
    "image_url": "https://malamute.com/img/boots.jpg",
    "url": "https://malamute.com/hiking-boots/trail-blazer"
  }
}
```

### `cart_add`

```json
{
  "event_type": "cart_add",
  "properties": {
    "cart_id": "cart-uuid",
    "product_id": "sku-12345",
    "product_name": "Trail Blazer Boots",
    "quantity": 1,
    "price": 149.99,
    "currency": "USD",
    "variant": "Brown / Size 11"
  }
}
```

### `cart_remove`

```json
{
  "event_type": "cart_remove",
  "properties": {
    "cart_id": "cart-uuid",
    "product_id": "sku-12345",
    "quantity": 1
  }
}
```

### `cart_viewed`

```json
{
  "event_type": "cart_viewed",
  "properties": {
    "cart_id": "cart-uuid",
    "item_count": 3,
    "total": 289.97,
    "currency": "USD",
    "items": [
      { "product_id": "sku-12345", "name": "Trail Blazer Boots", "quantity": 1, "price": 149.99 },
      { "product_id": "sku-67890", "name": "Wool Hiking Socks", "quantity": 2, "price": 14.99 }
    ]
  }
}
```

### `checkout_started`

```json
{
  "event_type": "checkout_started",
  "properties": {
    "cart_id": "cart-uuid",
    "order_value": 289.97,
    "currency": "USD",
    "item_count": 3
  }
}
```

### `purchase`

The most important e-commerce event.

```json
{
  "event_type": "purchase",
  "properties": {
    "order_id": "order-abc123",
    "cart_id": "cart-uuid",
    "total": 289.97,
    "subtotal": 264.97,
    "tax": 20.00,
    "shipping": 5.00,
    "discount": 0,
    "coupon": null,
    "currency": "USD",
    "payment_method": "credit_card",   // not stored beyond type; no PCI data here
    "items": [
      {
        "product_id": "sku-12345",
        "product_name": "Trail Blazer Boots",
        "category": "Footwear",
        "quantity": 1,
        "price": 149.99
      }
    ]
  }
}
```

### `refund`

```json
{
  "event_type": "refund",
  "properties": {
    "order_id": "order-abc123",
    "refund_amount": 149.99,
    "currency": "USD",
    "items": [
      { "product_id": "sku-12345", "quantity": 1, "refund_amount": 149.99 }
    ],
    "reason": "wrong_size"
  }
}
```

---

## Custom Events

Marketers can define and track any event not covered by the standard schema using `custom_event`.

```javascript
// Marketer-defined custom event
_sym('track', 'custom', {
  name: 'video_played',
  properties: {
    video_id: 'vid-001',
    video_title: 'How to Pack for a Backpacking Trip',
    duration_seconds: 342,
    pct_watched: 65
  }
});
```

Stored as:

```json
{
  "event_type": "custom",
  "properties": {
    "name": "video_played",
    "video_id": "vid-001",
    "video_title": "How to Pack for a Backpacking Trip",
    "duration_seconds": 342,
    "pct_watched": 65
  }
}
```

Custom event names are surfaced in the segmentation engine as queryable fields:
```sql
-- Contacts who watched the video
WHERE event_type = 'custom'
  AND properties->>'name' = 'video_played'
  AND (properties->>'pct_watched')::int > 50
```

### Custom Event Registry

Marketers may register custom event definitions to enable type validation and UI display:

```
POST /marketing/event-definitions

{
  "name": "video_played",
  "description": "A video was played on the site",
  "properties": [
    { "key": "video_id", "type": "text", "required": true },
    { "key": "pct_watched", "type": "number", "required": false }
  ]
}
```

Registering a definition is optional — unregistered custom events are stored as-is. Registration enables the segmentation engine UI to surface the event type and its properties as filterable fields.

---

## Session Events (Server-Produced)

### `session_expired`

Emitted when a site session exceeds the marketer-configured inactivity TTL. **TTL is not platform-global** — tenant default with optional per-site override. Full mechanics: [Session Model](./session-model.md).

Used as the primary trigger for cart-abandon and browse-abandon Campaigns.

```json
{
  "event_type": "session_expired",
  "source": "web",
  "site_id": "uuid",
  "session_id": "uuid",
  "properties": {
    "inactivity_ttl_seconds": 1800,
    "had_cart_activity": true,
    "had_purchase": false,
    "cart_id": "cart-uuid",
    "config_source": "site"
  }
}
```

---

## Automation Activity Events (Server-Produced)

Not emitted by the JS tracker. Produced by the Campaign and Journey executors and written to the same `marketing.contact_events` store so history branching, segmentation, and analytics share one timeline with web and email events.

Canonical list and property requirements: [Journeys — Activity Events and History Branching](../Journeys/journeys.md#activity-events-and-history-branching).

| `event_type` | Source |
|---|---|
| `journey_enrolled`, `journey_step_entered`, `journey_step_completed`, `journey_step_failed`, `journey_exited`, `journey_reentry_blocked` | Journey executor |
| `campaign_enrolled`, `campaign_send_queued`, `campaign_send_skipped`, `campaign_job_included` | Campaign executor / Broadcast freeze |
| `trigger_matched` | Trigger evaluator |

Example:

```json
{
  "event_type": "journey_step_completed",
  "source": "api",
  "contact_id": "uuid",
  "properties": {
    "journey_id": "uuid",
    "journey_version": 3,
    "campaign_id": "uuid",
    "step_id": "uuid",
    "step_name": "Welcome Email",
    "step_type": "action",
    "enrollment_id": "uuid",
    "outcome": "advanced"
  }
}
```

---

## Tracker JavaScript API

```javascript
// Initialize (required, in <head>)
_sym('init', { tenant_id: '...', brand_domain: '...', ... });

// Identify a visitor
_sym('identify', 'user@example.com', { first_name: 'Jamal', properties: { plan: 'Pro' } });

// Track a custom event
_sym('track', 'event_name', { key: 'value' });

// Track a page view manually (auto-tracked by default; use for SPAs)
_sym('page');

// Update consent
_sym('consent', { analytics: true, marketing: false });

// Opt out of Symposia network tracking
_sym('network_opt_out');

// Get visitor ID (for server-side event stitching)
_sym('get_visitor_id', function(id) { console.log(id); });
```

---

## Privacy Constraints on Event Data

The tracker must never capture:

- Passwords, credit card numbers, bank account numbers, SSNs, or any PCI/PII that appears in form fields. The `form_submit` event captures field names only, never values.
- Raw IP addresses (used for geo enrichment, then discarded).
- Raw user-agent strings beyond device/browser/OS parsing.
- Content of email bodies.
- Any data about individuals without applicable consent.

These constraints are enforced in the tracker code itself (client-side field filtering) and in the ingestion service (server-side validation that strips prohibited fields if present).
