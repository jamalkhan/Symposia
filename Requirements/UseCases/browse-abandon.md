# Use Case: Browse Abandon (E2E)

**Status: Specced**  
**Template key:** `browse_abandon`  
**MVP priority:** P1 (after cart abandon) — same session machinery, higher volume / lower intent

## Goal

Re-engage contacts who **viewed product or category pages** but did **not** add to cart or purchase before the session ended. Lower intent than cart abandon; stricter frequency control required.

---

## Detection model

### Primary: `session_expired` browse abandon

```
session with product/category views
  → no cart_add in session
  → no purchase in session
  → session_expired
  → trigger filters match
  → enroll journey-backed Campaign
```

**NATS:** `sym.{tenant_id}.events.web.session_expired`

**Trigger filter:**

```json
{
  "event_type": "session_expired",
  "all": [
    { "field": "properties.had_purchase", "op": "eq", "value": false },
    { "field": "properties.had_cart_activity", "op": "eq", "value": false },
    { "field": "properties.had_product_view", "op": "eq", "value": true },
    { "field": "contact_id", "op": "is_not_null" },
    { "field": "site_id", "op": "eq", "value": "{{ campaign.site_id }}" }
  ]
}
```

### Session payload extensions (required for browse)

Session service must set on expire (in addition to cart fields):

| Property | Meaning |
|---|---|
| `had_product_view` | ≥1 `product_viewed` or product pageview with product metadata |
| `had_category_view` | ≥1 category listing page with category metadata |
| `last_product_id` / `name` / `url` / `image_url` | Best candidate for email (last viewed product) |
| `last_category_id` / `name` / `url` | If only category browsed |
| `product_view_count` | Volume signal (optional filter `>= 2`) |

### Instrumentation

| Event | Required properties |
|---|---|
| `product_viewed` | `product_id`, `name`, `url`, `image_url`, `price` (optional) |
| `page_view` on PDP | Or map SPA routes → `product_viewed` |
| Category pages | `category_id`, `name` in page_view properties or dedicated event |

Without product metadata, browse abandon **should not fire** (avoid “you browsed our homepage” spam).

### Secondary: absence after product_viewed

Optional: trigger `product_viewed` → wait 2h → exit on cart_add/purchase → else email.  
**Default template uses session_expired** for consistency with cart abandon.

---

## Eligibility

Same as cart abandon marketing gates, plus:

| Check | Default |
|---|---|
| Re-entry cooldown | **7 days** (stricter than cart if marketer wants — template default 7) |
| Min product views | Optional `product_view_count >= 1` (default 1) |
| Exclude if cart abandon active | If contact has active cart-abandon enrollment, **skip browse** (cart wins) |
| Frequency cap | Platform marketing caps; browse is easy to over-send |

---

## Journey graph (default)

```
[Trigger: session_expired + browse filters]
        │
        ▼
[Wait: 0–30 min optional delay]
        │  exit: cart_add | purchase
        ▼
[Action: Email 1 — "Still thinking about it?"]
        │  context: last product or category
        ▼
[Wait: 48 hours]
        │  exit: cart_add | purchase
        ▼
[Branch: cart or purchase since enroll?]
    yes → Exit
    no  → [optional Email 2] → Exit
```

**MVP default:** Email 1 only (`include_second_email: false`) to reduce fatigue. Email 2 opt-in.

---

## Personalization context

```json
{
  "trigger_event": "session_expired",
  "event_data": {
    "browse_type": "product",
    "product_id": "sku_99",
    "product_name": "Trail Running Shoes",
    "product_url": "https://shop.example.com/products/trail-shoes",
    "image_url": "https://cdn.example.com/sku_99.jpg",
    "price": 129.99,
    "currency": "USD",
    "product_view_count": 3
  }
}
```

Category-only:

```json
{
  "browse_type": "category",
  "category_name": "Running Shoes",
  "category_url": "https://shop.example.com/c/running"
}
```

---

## Interaction with cart abandon

| Situation | Behavior |
|---|---|
| Session had cart activity | **Cart abandon** Campaign matches; browse does not (`had_cart_activity` false required) |
| Browse email sent, later cart abandon same week | Allowed if re-entry/caps allow; cart is higher intent |
| Both campaigns active | Trigger filters partition on `had_cart_activity` |

---

## Campaign defaults

| Field | Value |
|---|---|
| type | triggered / journey_backed |
| category / priority | marketing |
| re_entry | `re_entry_after_cooldown` **7 days** |
| site binding | Required |

---

## Anonymous browse

Same as cart: **no email without `contact_id`**. Optional 24h pending identify if product context retained.

---

## MVP vs P1

| Included when browse ships | Notes |
|---|---|
| session fields `had_product_view` etc. | Session service work |
| Template + Journey | Clone `browse_abandon` |
| Cart-priority exclusion | Filter only (no cross-campaign lock DB required) |

Not required for **first** MVP launch day if cart abandon ships first; should follow immediately (same session pipeline).

---

## Checklist

- [ ] Tracker emits `product_viewed` with metadata  
- [ ] Session expire sets browse flags + last product  
- [ ] Campaign filter excludes cart sessions  
- [ ] 7-day re-entry default  
- [ ] Single email default  
- [ ] Liquid product block  

---

## References

- [cart-abandon.md](./cart-abandon.md)  
- [session-model.md](../Tracking/session-model.md)  
- [event-schema.md](../Tracking/event-schema.md)  
- [campaigns.md](../Messaging/campaigns.md)  
- [journeys.md](../Journeys/journeys.md)  
