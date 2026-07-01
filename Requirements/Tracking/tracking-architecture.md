# Tracking Architecture

## Overview

The Symposia tracking system collects behavioral data about individuals as they interact with marketer websites and content. It is a two-layer system:

- **Brand-level (first-party)**: data collected on behalf of the marketer, stored under the marketer's domain. Each marketer brand has its own cookie identity.
- **Network-level (Symposia)**: a cross-brand identity that links behavior across multiple marketers on the network. Requires explicit individual consent.

The tracking system is the data collection side of the platform. It feeds the contact database, the segmentation engine, and the analytics layer.

**The data sovereignty principle applies here too**: the network-level cookie is opt-in, not opt-out. Individuals must affirmatively consent to cross-brand tracking. Brand-level tracking follows the marketer's own cookie consent policies (typically a cookie banner on their site), but the Symposia tracker respects whatever consent the marketer's consent management platform (CMP) records.

---

## Components

### 1. JavaScript Tracker Snippet

A small JavaScript snippet that marketers embed on their website (similar to Google Analytics gtag, Facebook Pixel, or Klaviyo's JS). The snippet is loaded async and non-blocking.

```html
<!-- Symposia Tracking Snippet -->
<script>
(function(s,y,m,p,o,s2,i,a){
  s['SymposiaObject']=o;s[o]=s[o]||function(){
  (s[o].q=s[o].q||[]).push(arguments)};
  a=y.createElement(m);a.async=1;
  a.src=p;i=y.getElementsByTagName(m)[0];
  i.parentNode.insertBefore(a,i)
})(window,document,'script','https://js.symposia.network/v1/tracker.js','_sym');

_sym('init', {
  tenant_id: 'tenant_01abc',
  brand_domain: 'malamute.com',
  enable_network_tracking: true,    // false to disable Symposia cross-brand cookie
  consent_mode: 'cmp',              // 'cmp' | 'explicit' | 'none'
  cmp_provider: 'onetrust'          // optional: CMP integration
});
</script>
```

The snippet is self-contained: a single async `<script>` tag. No GTM, no additional dependencies. Size target: < 8 KB minified + gzipped.

### 2. Tracking Pixel (Fallback)

A 1×1 transparent GIF served from `https://px.symposia.network/v1/track?params=...` for environments where JavaScript is not available (email tracking, AMP pages, server-side include contexts).

The tracking pixel is used for:
- **Email open tracking**: embedded in HTML email body as an `<img>` tag. When the email client loads the image, the open event is recorded.
- **Server-side tracking**: some CMSs or email templates cannot run JavaScript but can include an `<img>` tag.

The pixel URL encodes event data as URL parameters (base64-encoded, signed with an HMAC to prevent forgery). Example:
```
https://px.symposia.network/v1/track?e=eyJ0...HMAC...&t=email_open
```

### 3. Click Redirect Service

Links in marketing emails and tracked landing pages are rewritten as redirect URLs. When a recipient clicks a link, the redirect service:
1. Records the click event (contact ID, campaign ID, link URL, timestamp).
2. Redirects to the original URL.

Click redirect URLs:
```
https://click.symposia.network/r/{token}
```

Where `{token}` encodes the destination URL, contact identity, and campaign ID, signed with an HMAC.

---

## Cookie Model

### Brand-Level Cookie (First-Party)

**Name**: `_sym_brand` (configurable per tenant to e.g., `_mal_id` for Malamute)
**Domain**: The marketer's domain (e.g., `.malamute.com`)
**Expiry**: 2 years (rolling, refreshed on each page load)
**Type**: First-party cookie (set by marketer's own domain)
**Requires consent**: Yes, under PECR/GDPR where the marketer operates in covered jurisdictions. The marketer's own cookie consent banner governs this.

**Contents** (signed JWT stored in the cookie value):
```json
{
  "brand_visitor_id": "uuid",        // anonymous visitor ID for this brand
  "contact_id": "uuid",              // null until identified (form submit, login, email click)
  "tenant_id": "tenant_01abc",
  "created_at": "2026-06-01T00:00:00Z",
  "updated_at": "2026-06-30T12:00:00Z"
}
```

When a contact is identified (submits an email address on a form, clicks a tracked email link), the `contact_id` is written into the brand cookie. All anonymous events before this point are retroactively associated with the contact via the `brand_visitor_id`.

### Symposia Network Cookie (Third-Party / Cross-Brand)

**Name**: `_sym_net`
**Domain**: `.symposia.network` (a Symposia-owned domain, set as a third-party cookie from the marketer's domain perspective)
**Expiry**: 1 year
**Type**: Third-party cookie (in browsers that still allow third-party cookies, e.g., Firefox, Chrome with user permission)
**Requires consent**: Yes — explicit opt-in from the individual. This is not a "necessary" cookie; it requires affirmative consent. Default is not set.

**Browser third-party cookie restrictions**: Safari and Firefox block third-party cookies by default. Chrome has been phasing out third-party cookie support. The Symposia network cookie works where permitted but gracefully degrades:
- Where third-party cookies are blocked: network-level tracking falls back to a first-party proxy method (see First-Party Data Bridge below).
- If the individual has a Symposia account and is logged in on `profile.symposia.network`, a same-site partitioned cookie may provide continuity even where third-party cookies are blocked (using CHIPS — Cookies Having Independent Partitioned State).

**Contents** (signed, encrypted):
```json
{
  "network_visitor_id": "uuid",       // cross-brand anonymous visitor ID
  "symposia_identity_id": "uuid",     // null until identity link established
  "created_at": "...",
  "consent_granted_at": "...",
  "consent_source": "preference-center | tracker-banner | symposia-profile"
}
```

### First-Party Data Bridge (CNAME Proxy)

For marketers who want network-level tracking without relying on third-party cookies, the tracker supports a first-party proxy via CNAME:

1. The marketer adds a DNS CNAME: `sym.malamute.com` → `proxy.symposia.network`
2. The tracker is configured with `first_party_endpoint: 'https://sym.malamute.com'`
3. Requests to `sym.malamute.com` appear as first-party (same registrable domain for Safari ITP purposes) while being handled by Symposia's servers.
4. The network cookie is set on `sym.malamute.com` — still first-party from the browser's perspective.

This allows Symposia network tracking to work in Safari/Firefox without third-party cookies, but it is opt-in for the marketer and requires a DNS change.

---

## Consent Integration

### Banner Responsibility: Marketer-Primary, Symposia Fallback

The **marketer's cookie consent banner is the primary consent surface** for both cookies — the brand-level cookie (`_sym_brand`) and the Symposia network cookie (`_sym_net`). When a marketer deploys the Symposia tracker, their banner must explicitly name both cookies and their purposes. The marketer controls the wording, timing, and CMP integration; the platform provides guidance on required disclosures.

**If the Symposia tracker is installed but no marketer banner is detected** — meaning no CMP consent event fires within a configurable timeout (default: 3 seconds after tracker load) — the tracker automatically surfaces the **Symposia fallback banner**. This is a safety net, not the intended primary flow.

> **TODO: Legal review required for the Symposia fallback banner copy before launch.** Copy must cover: (1) what `_sym_brand` collects and on whose behalf, (2) what `_sym_net` collects and that it is cross-brand, (3) how to opt out later and that consent expires after 13 months, (4) that if the individual is already identified on the Symposia network and has previously consented, this banner may not appear, (5) links to the marketer's privacy policy and Symposia's network privacy policy. Jurisdiction-aware copy required (EU/UK vs. US vs. CA vs. others). Assign to legal counsel before tracking ships in any GDPR-covered market.
>
> **Placeholder copy (not legally reviewed — do not ship):**
> "[Marketer name] uses Symposia to personalize your experience and, with your permission, to recognize you across other brands on the Symposia network. Two cookies may be set: a brand cookie for [Marketer name] and a Symposia network cookie for cross-brand recognition. Your choice applies for 13 months or until our policy changes. If you have previously consented on the Symposia network with a verified identity, your prior choice may carry over. [Accept all] [Decline] [Manage preferences] [Learn more →]"

### Consent Modes

| Mode | Behavior |
|---|---|
| `cmp` | The tracker listens for consent events from a configured CMP (OneTrust, Cookiebot, Usercentrics, Osano). It only sets cookies after the CMP reports that the relevant consent category has been granted. The marketer's CMP banner must include Symposia cookie disclosures. |
| `explicit` | The marketer manages consent themselves. They call `_sym('set_consent', { analytics: true, marketing: true })` after collecting consent via their own UI. |
| `none` | No consent management. The tracker operates in full mode immediately. **Only valid in regions/contexts where consent is not legally required.** |

### Consent Categories Mapped to Tracking Behavior

| Consent Category | What It Enables |
|---|---|
| `necessary` / no consent | Nothing. The tracker loads but sets no cookies and collects no data. |
| `analytics` | Brand-level anonymous tracking (`_sym_brand` without `contact_id`). No cross-brand tracking. |
| `marketing` / `targeting` | Brand-level identified tracking + Symposia network cookie (if individual also consents at the Symposia level). |

### Consent Events

Consent is a first-class event — both when shown and when recorded — for two reasons: (1) it feeds the individual's consent record on their profile, and (2) consent events are included in the Merkle commitment pipeline so they are tamper-evidently auditable. See [Event Integrity](../Platform/event-integrity.md).

**Four consent events are defined** (full schemas in [Event Schema](./event-schema.md#consent-events)):

| Event | Emitted By | When |
|---|---|---|
| `cookie_consent_shown` | Marketer's banner / CMP | When the marketer's cookie consent UI is rendered to the visitor |
| `cookie_consent_recorded` | Marketer's banner / CMP | When the visitor makes a choice (accept/decline/customize) in the marketer's banner |
| `symposia_platform_cookie_consent_shown` | Symposia fallback banner | When the Symposia fallback banner is rendered (marketer banner not detected) |
| `symposia_platform_cookie_consent_recorded` | Symposia fallback banner | When the visitor makes a choice in the Symposia fallback banner |

### Consent Persistence

When a `cookie_consent_recorded` or `symposia_platform_cookie_consent_recorded` event fires, the consent decision is persisted in three places:

1. **Contact profile (Postgres)** — written to the contact's record as a permission grant entry (see [Permission Model](../Identity/user-data-ownership.md#permission-model)). If the visitor is anonymous (no `contact_id` yet), the consent is stored against the `brand_visitor_id` and migrated to the contact record when identification occurs.

2. **NATS compliance stream** — a `compliance.consent_granted` or `compliance.consent_revoked` event is published to `sym.{tenant}.compliance.consent_granted` (see [Queue and Pub/Sub](../Platform/queue-and-pubsub.md)). This triggers the Merkle commitment pipeline and creates a tamper-evident on-chain record of the consent decision.

3. **Symposia identity profile** — if the visitor has a linked `symposia_identity_id`, the consent grant is propagated to their cross-brand identity record, making it visible in the [Profile Portal](../Identity/user-profile-visibility.md) and enforceable across all marketers linked to that identity. For anonymous visitors without a linked identity, this propagation occurs at the time the identity link is established.

The combination of (2) and (3) means: consent decisions are cryptographically committed to the blockchain (via Merkle root) and visible to the individual in their own profile. A marketer cannot retroactively claim consent was given if the commitment record shows otherwise, and the individual can prove consent was or was not granted at a specific time.

### Banner Copy Versioning and Storage

Every version of every consent banner is persisted to blob storage at the time it is deployed. The `banner_copy_version` field in consent events is a pointer to the exact copy the individual saw — making the consent legally tied to specific wording that can always be retrieved.

**Marketer banner copy** (for marketers using `explicit` consent mode or a custom CMP integration):
```
{marketer-blob-account}/consent-banners/{banner_version}/copy.json
```
Marketers who use a third-party CMP (OneTrust, Cookiebot, etc.) are responsible for version-tracking their own copy within that system. The `banner_copy_version` they pass to the tracker is their CMP's version identifier.

**Symposia fallback banner copy**:
```
{platform-blob-account}/consent-banners/symposia-fallback/{version}/copy.json
```
The platform maintains all historical versions. A version is never deleted — only superseded. This means any consent record in the Merkle commitment pipeline can always be matched to the exact copy that was shown, indefinitely.

```json
// Example copy blob: consent-banners/symposia-fallback/v1.0/copy.json
{
  "version": "v1.0",
  "deployed_at": "2026-07-01T00:00:00Z",
  "locales": {
    "en": {
      "headline": "...",
      "body": "...",
      "accept_label": "Accept",
      "decline_label": "Decline",
      "learn_more_url": "https://symposia.network/privacy"
    }
  }
}
```

### Consent Expiry and Copy-Change Invalidation

A consent decision (accept or decline) is valid for **13 months** from `recorded_at` — consistent with the anonymous data retention window and identity re-verification cadence (see [Identity Proof and Claim](../Identity/identity-proof-and-claim.md)).

**Copy change takes precedence over the 13-month window.** When a new version of the banner copy is deployed — whether the marketer's copy or the Symposia fallback — any existing consent record issued under the previous version is marked `pending_reconsent`. On the individual's next visit, the banner is shown again regardless of how recently they last consented. The 13-month clock resets from the new consent decision.

This rule exists because a change to consent copy may expand the scope of what is being consented to. Prior consent cannot be assumed to cover new or expanded scope — the individual must see and acknowledge the new wording.

The consent record stored in Postgres carries both `banner_copy_version` (the version under which consent was given) and `expires_at` (13 months from `recorded_at`, or reset when copy changes). The tracker checks `expires_at` and `banner_copy_version` on each visit before deciding whether to suppress or show the banner.

### Consent Inheritance via Identity Linking

When two cookie IDs are determined to belong to the same individual — through the identity claim process (see [Identity Proof and Claim](../Identity/identity-proof-and-claim.md)) — the platform checks whether any of the linked identities already holds a valid, unexpired consent record for the current copy version.

**If a valid consent record exists on any linked identity: consent is inherited.** The newly linked cookie ID adopts the existing consent decision. The individual is not shown the banner again. The inherited consent record is written with:
- `consent_source: "inherited_from_identity_link"`
- The original `recorded_at` timestamp (the 13-month clock continues from when consent was first given, not from when the link was established)
- A reference to the source identity/cookie from which consent was inherited

**If no valid consent exists on any linked identity**: the banner is shown on next visit as normal.

Consent inheritance does not cross copy versions. If the linked identity consented under `v1.0` and the current copy is `v2.0`, the consent is not inherited — the individual must re-consent to the new copy.

---

## Event Collection

See [Event Schema](./event-schema.md) for the full event specification.

Events flow from the tracker to the platform:

```
Browser
  │  _sym('track', 'page_view', { url: '...', title: '...' })
  │
  ▼
https://events.symposia.network/v1/collect   (HTTPS POST, batched every 5s or on page unload)
  │
  ▼
Event Ingestion Service (validates, authenticates, enriches with server-side context)
  │
  ├──► Brand-level event storage → marketer's contact_events table (Postgres)
  │
  └──► Network-level event storage → Symposia identity event log (if network consent granted)
```

Events are sent in batches (up to 20 events per request) to reduce HTTP overhead. The `sendBeacon` API is used for final batch sends on page unload, which is more reliable than XHR for end-of-session events.

### Server-Side Enrichment

When events arrive at the ingestion service, they are enriched server-side with:
- IP geolocation (country, region, city) — IP address is never stored, only the derived geo
- User-agent parsing (browser, OS, device type) — UA string is not stored beyond parsing
- Referrer parsing (source, medium, campaign from UTM parameters)
- Session attribution: is this a new session or continuation of an existing one?

The IP address and raw user-agent are used only for enrichment and are discarded. This is a deliberate privacy choice: the platform stores derived attributes (location, device type) but not the raw signals.

---

## Email Tracking

### Open Tracking

The personalization engine injects a tracking pixel into every HTML email before send:

```html
<img src="https://px.symposia.network/v1/track?e={encoded_token}" 
     width="1" height="1" alt="" style="display:block;visibility:hidden;" />
```

The token encodes: `send_id`, `contact_id`, `tenant_id`, `campaign_id`. When the image is fetched, the open event is recorded.

**Apple Mail Privacy Protection (MPP)**: Since iOS 15, Apple Mail prefetches tracking pixels, making opens unreliable. The platform detects MPP-inflated opens (requests from Apple's proxy IP ranges) and marks them as `open_type: machine` vs. `open_type: human`. Reported open rates surface both, and campaign analytics default to showing human opens.

### Click Tracking

Links in templates are rewritten by the personalization engine:
```
Original:  https://malamute.com/hiking-boots
Rewritten: https://click.symposia.network/r/{token}
```

The redirect service records the click, then issues an HTTP 302 to the original URL. Click events are recorded in the contact's event history.

**Unsubscribe link**: The unsubscribe link is also a click-tracked redirect, which simultaneously records the click and triggers the unsubscribe processing.

---

## Data Storage Routing

Tracked events are routed to the appropriate storage based on type and marketer configuration:

| Event Source | Data Goes To |
|---|---|
| Email opens and clicks | Marketer's Postgres `contact_events` table |
| Web tracking (brand-level) | Marketer's Postgres `contact_events` table |
| Web tracking (network-level) | Symposia identity event log (separate, platform-managed, individual-controlled) |
| High-volume event streams (>10K events/day) | Event queue → batch write to Postgres; optionally synced to marketer's analytics store |
| Purchase and e-commerce events | Marketer's Postgres + optionally the analytics layer when provisioned |

---

## Open Questions

- **First-party vs third-party cookie future**: With third-party cookies increasingly restricted, is the Symposia network cookie viable long-term? The CNAME proxy approach works but requires marketer DNS configuration. Should the platform invest in a first-party-only identity approach from the start (each marketer's cookie, unified server-side)?
- **Tracking without cookies (fingerprinting)**: Some platforms use probabilistic fingerprinting (IP + user-agent + screen resolution hash) to track individuals without cookies. This is increasingly illegal in the EU. Symposia should explicitly prohibit fingerprinting-based tracking in the AUP and not offer this as a platform feature.
- **Is the tracking pixel MVP or phase 2?**: See [Todo.md](../Todo.md#open-architecture-questions).
