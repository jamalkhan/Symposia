# Symposia Data Cloud

## Overview

The Symposia Data Cloud is the platform's canonical, cross-brand view of every individual who has interacted with any Symposia-powered marketer or appbuilder. By being present as a tracking layer across thousands of websites and apps, Symposia accumulates a unified behavioral and demographic profile for each individual that no single marketer could build on their own.

This positions Symposia as a **consumer data platform** comparable to Experian, Equifax, or Acxiom — but with a fundamental structural difference: the individual is in the driver's seat. They can see what Symposia knows about them, correct it, restrict it, or delete it. Marketers access Data Cloud attributes only with the individual's explicit permission and only on a paid Data Cloud licensing tier.

The Data Cloud is not a surveillance product sold behind closed doors. It is a **trust-based data exchange**: individuals get visibility and control they've never had before; marketers get higher-quality, higher-consent data that outperforms anything bought from a broker.

---

## How Data is Collected

Symposia's tracking layer operates on every marketer website and app where the JS tracker snippet or tracking pixel is installed (see [Tracking Architecture](../Tracking/tracking-architecture.md)). Two cookies are set per the consent model:

- `_sym_brand` — first-party cookie scoped to the marketer's domain. Owned by the marketer.
- `_sym_net` — Symposia's network cookie. Requires explicit opt-in under the `web_tracking_network` permission. This is the Data Cloud signal.

When an individual has granted `web_tracking_network` permission (default: `brand_only` for new visitors; network cookie requires active opt-in), their behavioral signals flow into the Symposia Data Cloud across every site and app they visit. Over time, form fills, purchase flows, and behavioral patterns across those sites allow Symposia to build up the attributes below.

**Data sourcing methods:**
- **Direct observation**: form fills that include name, email, phone, address (seen when the Symposia cookie is present and the individual submitted a form)
- **Cross-site pattern matching**: e.g., an email address seen on Site A and Site B with the same Symposia cookie ID links those sessions to the same individual
- **Behavioral inference**: shopping time patterns, browsing category patterns, purchase frequency, average order value across the network
- **ML-derived scoring**: Symposia computes propensity and affinity scores from aggregated network signals (see [Derived Attributes](#derived-attributes))

---

## Individual Profile Schema

The Symposia-managed individual profile. All attributes are owned by or controlled by the individual per the [Data Ownership Model](#data-ownership-model).

### Core Identity

These are identity-layer fields — directly identifying, owned by the individual, deleted outright on a right-to-delete request.

```
emails[]            List of email addresses seen across the network (with sources)
phones[]            List of phone numbers seen across the network
first_name          Best-known first name
last_name           Best-known last name
display_name        Preferred display name (self-reported if Symposia account exists)
addresses[]         Mailing/shipping addresses seen across the network
wallet_address      Blockchain wallet address (authoritative identity anchor)
```

Multiple values (emails, phones, addresses) are stored with provenance — which site or context each was observed in — so the individual can review and selectively revoke each one.

### Demographics

Inferred from cross-site behavioral signals. These are created/derived-layer attributes — Symposia is the creator, individuals are the subject. On a deletion request, these are anonymized rather than deleted outright.

```
date_of_birth           Date or estimated birth year (inferred from signals)
age_range               Bucketed age range (e.g. 25–34) when DOB is unknown
gender                  Inferred or self-reported
household_income_range  Estimated household income bracket (inferred)
household_size          Estimated number of people in household (inferred)
education_level         Inferred (e.g., high school / college / postgraduate)
occupation              Job title or occupational category (inferred from form fills, LinkedIn signal)
employer                Employer name (inferred, where available)
marital_status          Single / partnered / married (inferred from purchase patterns)
has_children            Boolean or estimated age range of children (inferred)
homeowner_status        Owner / renter / unknown (inferred from purchase signals)
life_stage              Student / young professional / new parent / retiree / etc. (ML-inferred)
```

### Geographic

```
primary_location        Best-known city / region / country
timezone                Inferred from behavioral timing patterns
mobility_pattern        Highly local / regional / national traveler (inferred)
```

### Behavioral

Inferred from cross-site network signals. Created/derived layer — anonymized on deletion.

```
preferred_shopping_times        Distribution of activity by hour of day and day of week
preferred_devices               Mobile / desktop / tablet, ranked by usage share
preferred_channels              Email / web / app, ranked
shopping_frequency              Estimated transactions per month across the network
average_order_value             Estimated AOV across the network
recency                         Days since last observed network activity
```

### Derived Attributes

ML-computed by Symposia from network-wide aggregated signals. These are scored and updated on a recurring cadence (e.g., weekly).

```
brand_affinities[]              Top-N brands, each with an affinity score (0–1)
                                e.g. [{ brand: "Nintendo", score: 0.94 }, ...]
category_affinities[]           IAB taxonomy category affinities, scored
                                e.g. [{ category: "Video Games", score: 0.92 }, ...]
purchase_propensities[]         Likelihood to purchase in a given category (0–1)
email_engagement_tier           Highly Engaged / Moderate / Low / Inactive
churn_propensity                Likelihood to go inactive in the next 90 days (0–1)
price_sensitivity               Premium / Value / Deal-Driven (inferred)
```

Propensity and affinity models are computed on **aggregated, anonymized network data** and then scored against the individual's own signals. The underlying model training does not expose individual-level records to any marketer.

---

## Data Ownership Model

### The Three-Party Model

| Party | Role | Data Rights |
|---|---|---|
| **Individual** | The data subject | Owns identity-layer data outright. Ultimate authority over access, rectification, and deletion of all data about them — including derived data. |
| **Symposia** | Data steward and processor | Collects behavioral signals with consent. Derives attributes from aggregate network signals. Retains the right to use anonymized, aggregated data to build and improve propensity and affinity models. Does not sell individual-level data. |
| **Marketer / Appbuilder** | Data steward and processor | Owns behavioral and transactional events they directly observed (email opens, purchases, page views on their site). Does not own the individual's identity-layer data — they hold it under permission. |

### Shared Ownership of Demographic Data

Marketers agree in Symposia's Terms of Use that **demographic and buyer-profile data collected through their Symposia tracking integration is jointly stewarded by Symposia and the marketer**. Specifically:

- When a marketer's site or app contributes a signal (a form fill, a purchase category, a browsing pattern) that feeds the Data Cloud, Symposia holds the right to incorporate that signal into the individual's cross-brand profile.
- The marketer retains their own copy of the event in their contact database, owned by them.
- Neither party owns the signal exclusively — the individual retains ultimate authority.

**What the marketer owns exclusively:** Behavioral and transactional events tied to their own brand relationship. Order history at Marketer A, campaign engagement history at Marketer A, custom properties Marketer A computed — these are Marketer A's data, not Symposia's. Symposia does not claim ownership of marketer-specific transactional records.

### Symposia's Aggregation Rights

Symposia withholds the right to use **aggregated, anonymized** data across the network for the sole purpose of:
- Building and improving individual profile attributes (the schema above)
- Training propensity and affinity models (brand affinity, category affinity, purchase propensity, etc.)
- Deriving behavioral patterns (time-of-day activity, shopping frequency) used to score individual profiles

This right is exercised only at the aggregate/anonymized level for model training. Individual-level records are not shared between marketers and are not used to train models sold or licensed to third parties.

---

## Individual Rights and Controls

The individual has granular control over what Symposia knows about them and what flows downstream to marketers.

### What the Individual Can See

Via the [User Profile Visibility](../Identity/user-profile-visibility.md) portal:

- All identity-layer attributes Symposia holds about them
- All derived attributes (brand affinities, propensity scores, demographic inferences) and how they were derived
- Which marketers have a linked contact record
- Which marketers have access to which Data Cloud attributes (and which permission tier)
- Marketer-created enrichment attributes that have been flagged as `symposia_visible` (see [Marketer Enrichment Data](#marketer-enrichment-data))
- Where Symposia's derived attributes and a marketer's derived attributes differ in value for the same attribute name (namespace-separated display)

### What the Individual Can Do

| Action | Scope | Effect |
|---|---|---|
| **View** | All Symposia-held attributes | Read-only portal access — no permission required |
| **Correct** | Identity-layer attributes | Updates Symposia's record; propagates to linked marketer contact records via rectification event (see [Rectification Propagation](../Identity/user-data-ownership.md#rectification-propagation)) |
| **Revoke Data Cloud access** | Per marketer | Removes marketer's ability to query Symposia-derived attributes for this individual; does not delete the marketer's own contact record |
| **Revoke network tracking** | Platform-wide | Sets `web_tracking_network = blocked`; Symposia stops collecting new cross-brand signals; existing profile data is retained unless deletion is also requested |
| **Anonymize** | Derived/demographic attributes | Symposia's inferred demographic and behavioral attributes are anonymized for this individual; individual remains in aggregate model training as an anonymous datapoint |
| **Pseudonymize** | Full profile | Identity-layer fields severed; all derived data detached from any identifying value and retained as an anonymous profile |
| **Delete** | Full profile (right to erasure) | Identity-layer fields deleted outright; derived/demographic data anonymized per erasure policy; erasure hash recorded to prevent re-identification via re-import; propagation event sent to all linked marketers |

---

## Data Cloud Access for Marketers

### Requirements

A marketer accessing Symposia Data Cloud attributes for an individual must satisfy **both** conditions:

1. **Individual consent**: The individual must have an active `data_read` permission grant for this marketer (see [Permission Model](../Identity/user-data-ownership.md#permission-model)).
2. **Data Cloud licensing tier**: The marketer's account must be subscribed to the Data Cloud tier. This is a paid add-on above the base platform subscription. (Billing and tier definitions: TBD — to be defined in a separate pricing/billing spec.)

Neither condition alone is sufficient. An individual's consent does not unlock Data Cloud data if the marketer is not on the tier; a marketer on the Data Cloud tier cannot access data without the individual's explicit grant.

### What Marketers Can Access

On the Data Cloud tier with a valid `data_read` grant, a marketer can query:

- The individual's Symposia-derived demographic attributes (age range, income bracket, household size, life stage, etc.)
- Brand and category affinities as computed by Symposia's models
- Behavioral patterns (shopping frequency, preferred times, preferred channels)
- Propensity scores (purchase propensity by category, email engagement tier)

Marketers **cannot** access:
- Another marketer's contact-level data (ever, regardless of tier)
- Identity-layer attributes beyond what the individual has explicitly shared with them via `data_read`
- The underlying training data or model weights behind Symposia's propensity scores
- Other individuals' profiles — queries are always scoped to a single individual via `symposia_identity_id`

### Namespace Separation

Symposia's derived attributes and a marketer's own derived enrichment attributes occupy **separate namespaces**. They are never merged and do not overwrite each other.

**Example**: Both Symposia and Gap (a marketer) compute a `brand_affinity` attribute for an individual:

| Namespace | `brand_affinity` value |
|---|---|
| `symposia.brand_affinity` | `[{ brand: "Nintendo", score: 0.94 }, { brand: "Sony", score: 0.81 }]` |
| `gap.brand_affinity` | `[{ brand: "Gap", score: 0.88 }, { brand: "Banana Republic", score: 0.72 }]` |

These are surfaced separately to:
- **The individual** in the profile portal (both are visible; the source is labeled)
- **The marketer** via API (they see only their own namespace by default; they can query `symposia.*` with a valid `data_read` grant)
- **Appbuilders** (same rules as marketers; they see the namespace(s) they have permission to access)

If a marketer licenses Symposia's `brand_affinity` via Data Cloud, they receive Symposia's value as a separate field — they do not replace or merge it with their own.

---

## Marketer Enrichment Data

Source of truth for `data_enrichment` permission mechanics.

### What Marketers and Appbuilders Can Enrich

With the `data_enrichment` permission granted by the individual, a marketer or appbuilder can create derived attributes about that individual based on their own data and analysis. Examples:

- **Brand affinity** (from the marketer's own purchase and engagement history)
- **Product propensity scores** (computed by the marketer's own ML models)
- **Buyer behavioral profiles** (e.g., "high-LTV loyalist," "discount-driven acquirer")
- **Marketing list or bucket membership** (e.g., "VIP tier," "reactivation candidate")
- **Custom ML scores** (churn propensity, upsell readiness, category interest)
- **Lifestyle or life-stage signals** derived from purchase patterns the marketer has observed

There is no closed list of permitted attribute names — marketers and appbuilders can define any attribute relevant to their business. The constraint is that these attributes may only be derived from data the individual has already permitted them to hold (they cannot derive enrichment from data they do not own or are not licensed to use).

### Where This Data Lives

Enrichment data created by a marketer or appbuilder lives in the **marketer's own tenant Postgres** in a dedicated `marketing.contact_enrichment` table (see schema in [Contact Database](../MarketingData/contact-database.md#contact-enrichment)). It is NOT stored in Symposia's central data store.

The `symposia_visible` flag on each enrichment attribute controls whether that attribute is surfaced to the individual via the profile portal. When the individual views their profile portal, the platform calls each linked tenant's API to aggregate `symposia_visible = true` enrichment attributes. These are displayed alongside Symposia's own derived attributes, with the marketer/appbuilder clearly identified as the source.

### Deletion Obligation

On a right-to-delete request, enrichment attributes in the marketer's database are handled per the [Erasure and the Created-Data Layer](../MarketingData/contact-database.md#erasure-and-the-created-data-layer) rules. Enrichment attributes created by the marketer are the marketer's created/derived data — they are anonymized, not deleted outright. Enrichment attributes created by a licensed appbuilder carry the appbuilder's `owner_id` and the anonymization obligation routes to the appbuilder.

---

## Relationship to Existing Requirements

| Requirement | Location |
|---|---|
| Tracking data collection and cookie consent model | [Tracking Architecture](../Tracking/tracking-architecture.md) |
| Identity-layer vs. created/derived-layer ownership | [Contact Database — Data Ownership Model](../MarketingData/contact-database.md#data-ownership-model) |
| Individual's right to delete | [Right to Delete](../Identity/right-to-delete.md) |
| Subscription and preference management | [Subscription Management](../Identity/subscription-management.md) |
| Profile portal (what the individual sees) | [User Profile Visibility](../Identity/user-profile-visibility.md) |
| Permission model and grants | [User Data Ownership](../Identity/user-data-ownership.md) |
| Rectification event propagation | [User Data Ownership — Rectification Propagation](../Identity/user-data-ownership.md#rectification-propagation) |
| Contact enrichment schema (marketer Postgres) | [Contact Database — Contact Enrichment](../MarketingData/contact-database.md#contact-enrichment) |
| Platform identifier index | [Contact Database — Platform Identifier Index](../MarketingData/contact-database.md#platform-identifier-index) |
