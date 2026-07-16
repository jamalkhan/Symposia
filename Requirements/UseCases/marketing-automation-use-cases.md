# Marketing Automation Use Cases

These are **first-class platform use cases** — not just examples in documentation, but capabilities the platform is explicitly designed to support end-to-end. Each use case exercises the [Segmentation Engine](../MarketingData/segmentation-engine.md), [Journey Engine](../Journeys/journeys.md), [Tracking System](../Tracking/tracking-architecture.md), and [Pub/Sub layer](../Platform/queue-and-pubsub.md) together.

Status labels: **Stub** = structure defined, details TBD | **Specced** = fully detailed elsewhere | **In Progress** = partially defined

---

## 1. Cart Abandon

**Status: Specced** — full E2E: **[cart-abandon.md](./cart-abandon.md)** (MVP primary: `session_expired` + cart flags). Journey wait/absence patterns remain in [Journeys](../Journeys/journeys.md#absence-timeout-triggers-the-cart-abandon-pattern).

A contact has cart activity and leaves without purchasing. Session TTL expiry emits `session_expired`; a journey-backed Campaign sends recovery email(s).

**Trigger (MVP primary)**: `session_expired` where `had_cart_activity && !had_purchase`  
**Trigger (optional alternate)**: `cart_add` → wait → no purchase  
**Journey**: Optional cool-down → Email 1 → Wait 24h → Branch → Email 2  
**Key context**: Cart contents, cart total, cart URL, product images  
**Re-entry**: cooldown 7 days (default)

---

## 2. Browse Abandon

**Status: Specced** — **[browse-abandon.md](./browse-abandon.md)**

A contact views product/category pages but does not cart or purchase before session end. Lower intent than cart; stricter re-entry.

**Trigger (primary)**: `session_expired` with `had_product_view && !had_cart_activity && !had_purchase`  
**Journey**: Optional delay → Email 1 → optional Email 2 after 48h  
**Re-entry**: 7-day cooldown; cart abandon takes priority when cart activity present

---

## 3. Back in Stock

**Status: Stub**

A contact expressed interest in a product that was out of stock (viewed the product page, attempted to add to cart, or signed up for a restock notification). When the marketer marks that product as back in stock, interested contacts are notified.

**Trigger**: Marketer-pushed event via External API (`sym.{tenant}.journey.enroll`) or a `web.custom` event (`event_name: product_back_in_stock`) with a `sku` or `product_id` in the payload  
**Segment**: Contacts who:
- Viewed the specific product page while it was out of stock, OR
- Explicitly signed up for a "notify me when available" form (added to a list), OR
- Had the product in an abandoned cart

**Journey**:
- Trigger event arrives with product context
- Immediately: send "Back in stock" email
- Wait 48h → Branch (purchased the product?) → Exit if yes; optional follow-up ("Selling fast — don't miss out") if no

**Key context**: Product name, SKU, product URL, price, product image  
**Notes**:
- The "notify me" signup should create a dedicated list (e.g., `restock_sku_99`) so the segment is a simple list membership check, not a behavioral query
- If using behavioral signals (viewed product page), the segment query joins `contact_events` — async evaluation likely needed for large catalogs
- The marketer's system (ecomm platform) is responsible for pushing the restock event; this Journey is reactive to it

---

## 4. Price Drop

**Status: Stub**

A contact showed interest in a product at its original price (viewed page, abandoned cart) but did not purchase. When the marketer drops the price, those contacts are notified.

**Trigger**: Marketer-pushed event (`web.custom` with `event_name: product_price_drop`) or External API call with product ID, old price, and new price in the payload  
**Segment**: Contacts who:
- Viewed the specific product page, OR
- Had the product in an abandoned cart, OR
- Are in a "price drop watchlist" list (explicit interest signal)

**Journey**:
- Trigger arrives with product + price context
- Immediately: send "Price just dropped" email
- Wait 72h → Branch (purchased?) → Exit or optional follow-up

**Key context**: Product name, SKU, old price, new price, discount percentage, product URL, product image  
**Notes**:
- Discount percentage should be computed at event time by the marketer's system and included in the trigger payload
- Frequency consideration: a product may drop price multiple times; re-entry policy should be "re-entry if not active" or "cooldown 30 days"
- Same behavioral segment caveat as Back in Stock — list-based is simpler and faster than event-join-based at scale

---

## 5. List Signup (Welcome Series)

**Status: Specced** — **[welcome-series.md](./welcome-series.md)**

Onboard newly **subscribed** contacts with a short email series. Starts only after DOI when required.

**Trigger**: `list.member_added` / subscribed `contact.created` / DOI handoff  
**Journey**: Email 1 → wait 2d → Email 2 → wait 3d → Email 3  
**Re-entry**: `no_re_entry` (once per contact)

---

## 6. Double Opt-In

**Status: Specced** — **[double-opt-in.md](./double-opt-in.md)**

Confirm email ownership and consent before marketing. Sets `pending` → `subscribed` or `unconfirmed`; emits Merkle-eligible `compliance.consent_granted`.

**Trigger**: pending contact / DOI-required list add  
**Journey**: Confirm email → wait 7d for click → enroll Welcome or mark unconfirmed  
**List flag**: `double_opt_in_required`

---

## 7. Brand Affinity — New Release

**Status: Stub**

A marketer releases a new product. Contacts who have demonstrated high affinity for the brand (either via Symposia Data Cloud scores or the marketer's own enrichment attributes) are notified first — ahead of or instead of a general broadcast.

**Trigger**: Marketer-pushed event (`web.custom` with `event_name: new_product_release`) or External API call with product and brand metadata  
**Segment**:
- **Option A (marketer's own data)**: Contacts where `enrichment.brand_affinity` score for this brand is above threshold (e.g., `> 0.7`) — evaluates from `marketing.contact_enrichment` in the marketer's Postgres
- **Option B (Symposia Data Cloud)**: Contacts with a linked `symposia_identity_id` + active `data_read` grant, where Symposia's `brand_affinity` score for this brand is above threshold — Phase 2, see [Data Cloud Segments](../MarketingData/segmentation-engine.md#data-cloud-segments-phase-2)

**Journey**:
- Trigger arrives with product context
- Send "New arrival — we think you'll love this" email to high-affinity contacts
- Wait 3 days → Branch (clicked or purchased?) → Exit if yes; optional follow-up

**Key context**: Brand name, product name/SKU, product URL, product image, affinity score used for targeting (for analytics)  
**Notes**:
- The brand affinity score used for targeting comes from the marketer's `contact_enrichment` table (Option A) or from a Symposia Data Cloud query (Option B, Phase 2 only)
- Gap example: Gap releases a new men's t-shirt line. Their `brand_affinity` enrichment score identifies their top 20% most loyal customers. Those contacts receive an early access email 48 hours before the general list.
- This use case demonstrates the value of the marketer maintaining their own enrichment data via `data_enrichment`

---

## 8. Category Affinity — New Release

**Status: Stub**

Similar to Brand Affinity — New Release, but targeting is based on category interest rather than brand loyalty. Useful when the marketer carries multiple brands or categories and wants to target based on what a contact buys, not just that they buy from this marketer.

**Trigger**: Marketer-pushed `new_product_release` event with category metadata in the payload  
**Segment**:
- Contacts where `enrichment.category_affinity` for the relevant IAB/product category is above threshold
- **Option B (Symposia Data Cloud)**: Contacts where Symposia's cross-brand `category_affinities` score for the relevant category is above threshold — Phase 2

**Journey**:
- Send "New in [Category Name]" email to high-category-affinity contacts
- Wait 3 days → Branch (clicked or purchased?) → Exit or follow-up

**Key context**: Category name, product name/SKU, product URL, product image  
**Notes**:
- Nintendo example: Jamal has a high `category_affinity` for Video Games across the network (Symposia Data Cloud), and a marketer selling gaming peripherals can (with `data_read` permission and Data Cloud tier) target him based on that cross-brand signal rather than only their own behavioral history with him
- For marketers without Data Cloud access, the category affinity score must come from their own enrichment data — derived from their own purchase/browse history with that contact
- Category taxonomy should align with IAB Tech Lab content taxonomy for interoperability with ad platform integrations

---

## Use Case × Platform Capability Matrix

| Use Case | Trigger Event | Segment Type | Journey Pattern | Context Data Required |
|---|---|---|---|---|
| Cart Abandon | `web.add_to_cart` | Behavioral (event) | Absence/timeout | Cart contents, cart URL |
| Browse Abandon | `web.pageview` | Behavioral (event) | Absence/timeout | Page URL, product/category |
| Back in Stock | `web.custom` / API push | List membership or behavioral | Immediate notify | Product SKU, product URL |
| Price Drop | `web.custom` / API push | List membership or behavioral | Immediate notify | Product, old/new price, savings % |
| List Signup | `contact.created` / form submit | Status (`pending` or `subscribed`) | Linear series | Signup source, offer |
| Double Opt-In | `contact.created` | Status (`pending`) | Wait + condition | Confirmation token |
| Brand Affinity New Release | `web.custom` / API push | Enrichment attribute (brand score) | Immediate notify | Brand, product, affinity threshold |
| Category Affinity New Release | `web.custom` / API push | Enrichment attribute (category score) | Immediate notify | Category, product, affinity threshold |
