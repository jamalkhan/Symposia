# Contact Database

## Overview

The contact database is the marketer's operational view of their customers and prospects. It stores structured profile data — name, email, phone, custom properties, tags, and compliance records — that feeds the segmentation engine, personalization engine, and email delivery system.

**Storage decision**: The contact database lives in the marketer's provisioned **Postgres database** (the same Neon-architecture Postgres specced in [Database Architecture](../Database/postgres-architecture.md)). This is the right call because:
- Contacts need fast, indexed operational queries (segmentation, lookup by email, count by property).
- The data model is relational and benefits from schema enforcement and joins.
- Blob storage is wrong here: you can't efficiently query across millions of contact rows stored as blobs.
- The analytics layer (DuckDB/ClickHouse, future) will receive synced/exported snapshots of contact data for analytical workloads — it does not replace the operational store.

Each marketer tenant provisions one or more Postgres databases. The contact database schema lives within the tenant's Postgres instance, in a dedicated schema (`marketing`).

---

## Data Ownership Model

This is the most important section in this document. Everything else here — the schema, the import/export flows, the API — is downstream of one rule, defined in full in [Stakeholders and Personas](../Platform/stakeholders-and-personas.md) and [User Data Ownership](../Identity/user-data-ownership.md):

> **Data that identifies an individual is owned by the individual. Data that is created about an individual is owned by whoever created it** (the marketer or an appbuilder they've licensed data from).

The contact database is not simply "the marketer's data." It is a **marketer's permissioned view of an individual**, layered with the **marketer's own business records about that individual**. These two layers are conceptually — and in the schema, structurally — distinct:

| Layer | Examples | Owned By | On Deletion Request |
|---|---|---|---|
| **Identity layer** | email, phone, first/last name, postal address | The individual | Deleted outright |
| **Created/derived layer** | order history, purchase behavior, custom properties the marketer computed, ML/AI model scores, tags, loyalty tier | The marketer (or licensing appbuilder) | Anonymized or pseudonymized, not deleted — see [Erasure and the Created-Data Layer](#erasure-and-the-created-data-layer) |

Every contact record carries a reference back to the individual it describes — the `symposia_identity_id` — which is the individual's own wallet-backed identity, not something the marketer controls. The marketer's copy of the identifying fields (email, name, etc.) exists because the individual granted permission to hold it; it is not an independent marketer asset that happens to describe the same person. This is why the deletion behavior differs by layer: revoking access to the identity layer is straightforward (it was never the marketer's to begin with), while the created layer requires anonymization to preserve the marketer's legitimate business record without retaining the individual's identifiability.

### One Individual, Multiple Marketer Views

Two marketers may both have a contact record for the same individual, and the identifying data each holds may genuinely differ — because the individual gave each marketer different information, at different times, through different channels.

**Example**: Jamal has a Symposia identity. He shops at Walmart and stays at Hyatt hotels.
- Walmart's contact record for Jamal: `email = jamal@gmail.com`, first name "Jamal," last name "Khan."
- Hyatt's contact record for Jamal: `email = jamal@hotmail.com`, first name "Jamal," last name "Khan."

Both records link to the **same** `symposia_identity_id` once Jamal has confirmed both relationships (via the tracking system matching, or via the "claim my records" flow in his [profile portal](../Identity/user-profile-visibility.md)). But the two marketers are not looking at a shared, merged "Jamal record" — each holds their own identity-layer snapshot, current as of whatever Jamal has told them. The platform does not silently merge or cross-populate identifying fields between marketers; Walmart never sees `jamal@hotmail.com` and Hyatt never sees `jamal@gmail.com`, unless Jamal explicitly grants a marketer `data_read` permission to see attributes from his Symposia-level profile (see the permission model in [User Data Ownership](../Identity/user-data-ownership.md)).

What the shared `symposia_identity_id` *does* enable:
- A right-to-delete request submitted once, from Jamal's profile portal, propagates to both Walmart's and Hyatt's contact records (see [Right to Delete](../Identity/right-to-delete.md)).
- Jamal can see, in one place, that both Walmart and Hyatt hold a record about him — without either marketer seeing the other's data.
- A global opt-out ("don't email me from any Symposia marketer") is enforceable across both, even though their underlying email addresses for him differ.

### Data Provenance on the Created Layer

Because created/derived data may originate from the marketer's own activity *or* from a licensed appbuilder data product, every piece of derived data carries a provenance marker — who created it, and therefore who owns it and who is responsible for anonymizing it on a deletion request.

```sql
-- owner_type / owner_id added to derived-data tables (see Custom Properties, below)
owner_type   TEXT NOT NULL DEFAULT 'marketer',   -- 'marketer' | 'appbuilder'
owner_id     UUID NOT NULL                       -- tenant_id (marketer) or appbuilder_id
```

A marketer who licenses a propensity score from an appbuilder does not become the owner of that score — the appbuilder remains the owner of record, and is the party obligated to anonymize it on deletion (the platform routes the deletion obligation to the correct owner; the marketer doesn't have to track this manually). See [Right to Delete](../Identity/right-to-delete.md) for how multi-owner deletion propagation works.

---

## Data Model

### Contact Record

The `marketing.contacts` table is the core entity.

```sql
CREATE TABLE marketing.contacts (
  contact_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           UUID NOT NULL,                  -- which marketer tenant
  email               TEXT NOT NULL,
  email_verified      BOOLEAN DEFAULT FALSE,
  email_status        TEXT NOT NULL DEFAULT 'subscribed',
                      -- subscribed | unsubscribed | bounced | complained | deleted
  phone               TEXT,
  phone_status        TEXT DEFAULT 'subscribed',

  -- ═══════════════════════════════════════════════════════════════
  -- IDENTITY LAYER — owned by the individual, held under permission.
  -- Deleted outright (not anonymized) on a right-to-delete request.
  -- See "Data Ownership Model" above and Right to Delete.
  -- ═══════════════════════════════════════════════════════════════
  first_name          TEXT,
  last_name           TEXT,
  display_name        TEXT,
  contact_type        TEXT DEFAULT 'consumer',        -- consumer | business

  -- Location (identity layer — self-reported or directly provided)
  country             TEXT,
  region              TEXT,
  city                TEXT,
  postal_code         TEXT,
  timezone            TEXT,

  -- ═══════════════════════════════════════════════════════════════
  -- CREATED/DERIVED LAYER — owned by this marketer (tenant_id above
  -- is the owner). Anonymized/pseudonymized, not deleted, on a
  -- right-to-delete request. See "Erasure and the Created-Data Layer".
  -- ═══════════════════════════════════════════════════════════════
  company             TEXT,    -- inferred/observed, e.g. from email domain or enrichment
  job_title           TEXT,    -- inferred/observed
  website             TEXT,    -- inferred/observed

  -- Compliance (see email-compliance.md) — procedural record of how
  -- permission was obtained; retained per the audit log requirement
  -- even through anonymization (see Right to Delete).
  email_consent_basis             TEXT,               -- express | implied | none
  email_consent_recorded_at       TIMESTAMPTZ,
  email_consent_source            TEXT,
  email_consent_wording           TEXT,
  implied_consent_expires_at      TIMESTAMPTZ,
  jurisdiction                    TEXT,               -- US | EU | CA | UK | ...
  gdpr_consent_recorded_at        TIMESTAMPTZ,
  gdpr_consent_source             TEXT,
  gdpr_consent_wording            TEXT,

  -- Symposia Identity (see user-data-ownership.md)
  symposia_identity_id            UUID,               -- nullable; links to the individual's Symposia profile

  -- Metadata
  source              TEXT,                           -- how they were added (import, form, api, manual)
  source_detail       TEXT,                           -- e.g., form name, campaign name
  created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_activity_at    TIMESTAMPTZ,

  CONSTRAINT uq_tenant_email UNIQUE (tenant_id, email)
);

CREATE INDEX ON marketing.contacts (tenant_id, email_status);
CREATE INDEX ON marketing.contacts (tenant_id, country);
CREATE INDEX ON marketing.contacts (tenant_id, created_at);
CREATE INDEX ON marketing.contacts (tenant_id, last_activity_at);
CREATE INDEX ON marketing.contacts (symposia_identity_id) WHERE symposia_identity_id IS NOT NULL;
```

### Custom Properties

Marketers define custom properties specific to their business (plan tier, dog breed, LTV, acquisition channel, etc.). Rather than altering the contacts table schema, custom properties are stored in a JSONB column with a supporting property definition table for discoverability and type validation.

**Custom properties are, by default, created/derived-layer data** — they describe something the marketer observed, computed, or was told in the context of their own business relationship (an order total, a computed LTV, a support tier). They are owned by whoever created them, per the [Data Ownership Model](#data-ownership-model) above, and are anonymized rather than deleted on a right-to-delete request.

```sql
CREATE TABLE marketing.contacts (
  ...
  properties          JSONB NOT NULL DEFAULT '{}',   -- custom key-value store
  ...
);

CREATE INDEX ON marketing.contacts USING gin (properties);

-- Property definitions (for validation, UI display, and ownership/provenance)
CREATE TABLE marketing.contact_property_definitions (
  property_key    TEXT NOT NULL,
  tenant_id       UUID NOT NULL,
  label           TEXT NOT NULL,
  data_type       TEXT NOT NULL,   -- text | number | boolean | date | datetime | list
  description     TEXT,
  owner_type      TEXT NOT NULL DEFAULT 'marketer',  -- 'marketer' | 'appbuilder'
  owner_id        UUID,                              -- appbuilder_id when owner_type = 'appbuilder';
                                                       -- null/implied tenant_id when owner_type = 'marketer'
  created_at      TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (tenant_id, property_key)
);
```

A property sourced from a licensed appbuilder data product (e.g., `properties.churn_propensity_score` computed by a third-party model vendor) is registered with `owner_type = 'appbuilder'` and the vendor's `owner_id`. This is what lets the platform route a deletion request's anonymization obligation to the correct owner automatically — the marketer hosting the contact record doesn't have to manually track which fields they're allowed to merely stop sharing versus which fields they're obligated to scrub. See [Right to Delete](../Identity/right-to-delete.md).

Querying custom properties:
```sql
-- Find contacts where plan_tier = 'Pro'
SELECT * FROM marketing.contacts
WHERE tenant_id = $1
  AND properties->>'plan_tier' = 'Pro'
  AND email_status = 'subscribed';

-- Find contacts with LTV > 500
SELECT * FROM marketing.contacts
WHERE tenant_id = $1
  AND (properties->>'ltv')::numeric > 500;
```

### Lists and List Membership

Contacts belong to one or more **lists** (also called audiences). A list is a static collection of contacts — a snapshot. Segments (dynamic filters) are different from lists (see [Segmentation Engine](./segmentation-engine.md)).

```sql
CREATE TABLE marketing.lists (
  list_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     UUID NOT NULL,
  name          TEXT NOT NULL,
  description   TEXT,
  list_type     TEXT DEFAULT 'standard',   -- standard | seed | suppression
  created_at    TIMESTAMPTZ DEFAULT now(),
  contact_count INT DEFAULT 0              -- denormalized for performance
);

CREATE TABLE marketing.list_memberships (
  list_id       UUID NOT NULL REFERENCES marketing.lists(list_id),
  contact_id    UUID NOT NULL REFERENCES marketing.contacts(contact_id),
  subscribed_at TIMESTAMPTZ DEFAULT now(),
  source        TEXT,
  PRIMARY KEY (list_id, contact_id)
);
```

### Suppression List

The suppression list is a list with `list_type = 'suppression'`. Hard bounced, complained, and globally unsubscribed contacts are automatically added by the platform. Marketers may also manually add contacts.

The suppression list check at send time is a `NOT EXISTS` join against `list_memberships` for the tenant's suppression list — fast because of the primary key index.

**GDPR deletion**: When a contact is deleted (right to erase), the identity-layer fields are removed, but a hash of their email is retained in a `marketing.erasure_hashes` table. This prevents re-import of the same address from re-enabling marketing to someone who has been erased. The platform checks this table at import time.

```sql
CREATE TABLE marketing.erasure_hashes (
  email_hash    TEXT NOT NULL,              -- SHA-256 of lowercased email
  tenant_id     UUID NOT NULL,
  erased_at     TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (tenant_id, email_hash)
);
```

### Erasure and the Created-Data Layer

A right-to-delete request does not produce the same outcome for every column in `marketing.contacts`. The two layers defined in [Data Ownership Model](#data-ownership-model) above are handled differently:

| Layer | Fields | Action on Deletion Request |
|---|---|---|
| **Identity** | `email`, `phone`, `first_name`, `last_name`, `display_name`, `country`/`region`/`city`/`postal_code` | **Deleted outright.** Row-level removal of these values; the individual owned them and is exercising the right to revoke. |
| **Created/derived** | `properties` (JSONB custom properties — order history, computed scores, tags), `company`/`job_title`/`website` (observed/inferred), `marketing.contact_events` history | **Anonymized or pseudonymized**, not deleted. The marketer (or appbuilder, per the `owner_type`/`owner_id` provenance on each property — see [Custom Properties](#custom-properties)) retains the analytical shape of this data but loses the ability to tie it back to the individual. |

**Pseudonymization** (the default mechanism): the `contact_id` itself is retained as an opaque token, all identity-layer fields are nulled or removed, and the `symposia_identity_id` link is severed. The created-data layer (`properties`, event history) stays attached to the now-unlinkable `contact_id`, preserving cohort/aggregate analytical value (e.g., "this token had 6 orders totaling $940 over 14 months") without any path back to who it was. This satisfies erasure under regimes that accept pseudonymization as sufficient (see the jurisdictional research item in [Todo.md](../Todo.md)).

**Anonymization** (used where pseudonymization is judged insufficient — e.g., small cohorts where re-identification risk is high, or where a stricter jurisdiction's law requires it): in addition to pseudonymization, derived values that are themselves quasi-identifying in combination (e.g., an unusual purchase pattern, a rare zip+birthdate combination) are generalized or suppressed (e.g., postal code truncated to region, exact purchase dates bucketed to month) so that re-identification by combination of attributes is no longer practical.

The choice between pseudonymization and anonymization for a given deletion request is a platform-enforced policy decision (driven by jurisdiction and data sensitivity), not something left to each marketer's discretion — consistent with the principle that the ownership model is "a structural constraint," not a policy a marketer could opt out of (see [User Data Ownership](../Identity/user-data-ownership.md)).

This is also why `marketing.erasure_hashes` exists as a separate table from the contact row itself: the hash is what blocks re-identification via re-import, while the (now-anonymized) original `contact_id` and its created-data history can continue to exist, decoupled from any identifying value, for the owner's continued legitimate use.

### Events / Activity History

Contact activity (email sent, opened, clicked, unsubscribed, purchased) is written to an events table. This table is write-heavy and append-only — it will grow large for active tenants.

```sql
CREATE TABLE marketing.contact_events (
  event_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     UUID NOT NULL,
  contact_id    UUID NOT NULL REFERENCES marketing.contacts(contact_id),
  event_type    TEXT NOT NULL,    -- email_sent | email_opened | email_clicked |
                                  -- unsubscribed | bounced | complained |
                                  -- page_view | purchase | custom
  occurred_at   TIMESTAMPTZ NOT NULL,
  campaign_id   UUID,
  send_id       UUID,
  properties    JSONB DEFAULT '{}',  -- event-specific data (URL clicked, amount, etc.)
  source        TEXT                 -- email | web | api | import
);

CREATE INDEX ON marketing.contact_events (tenant_id, contact_id, occurred_at DESC);
CREATE INDEX ON marketing.contact_events (tenant_id, event_type, occurred_at DESC);
CREATE INDEX ON marketing.contact_events (tenant_id, campaign_id) WHERE campaign_id IS NOT NULL;
```

For high-volume event ingestion (web tracking events), writes go through the event queue first and are batch-inserted. Direct-write events (email delivery events from the sending pipeline) insert immediately.

The events table should be partitioned by `occurred_at` (monthly partitions) for large tenants to keep query performance manageable.

---

## Contact Import

Marketers frequently import contacts in bulk from CSV files or external systems.

### CSV Import Flow

1. Marketer uploads a CSV file via the API or UI to a blob storage staging bucket.
2. The import processor validates the file (encoding, column headers, required fields).
3. The processor runs compliance pre-checks:
   - Are consent fields present for EU contacts?
   - Are there email addresses that match the tenant's erasure hash list?
4. The marketer maps CSV columns to contact fields (or uses a saved field map).
5. The marketer acknowledges a compliance attestation if consent records are missing for any contacts.
6. The import runs in the background. Large imports (>100K rows) may take several minutes.
7. Import summary: rows processed, created, updated, skipped (suppressed, erased, invalid email).

### Duplicate Handling

The unique key is `(tenant_id, email)`. On import:
- If the email already exists: **upsert** — update fields that are present in the import file; do not overwrite fields that are blank in the import.
- The compliance fields are never overwritten by import unless the import explicitly includes them. Existing consent records are preserved.

### API Import (single contact)

```
POST /marketing/contacts

{
  "email": "user@example.com",
  "first_name": "Jamal",
  "last_name": "Khan",
  "country": "US",
  "properties": {
    "plan_tier": "Pro",
    "ltv": 480
  },
  "compliance": {
    "email_consent_basis": "express",
    "email_consent_recorded_at": "2026-06-01T12:00:00Z",
    "email_consent_source": "checkout_form",
    "email_consent_wording": "Sign me up for email updates."
  },
  "lists": ["list_id_1", "list_id_2"]
}
```

---

## Contact Export

Contacts can be exported to CSV or JSON via the API, subject to GDPR's data portability requirement. Exports include all fields the tenant has stored, including custom properties and compliance records.

```
POST /marketing/contacts/export

{
  "format": "csv",
  "filter": {
    "list_id": "list_id_1",
    "email_status": "subscribed"
  },
  "fields": ["email", "first_name", "last_name", "properties.plan_tier"]
}
```

The export is a background job that produces a blob in the tenant's storage, downloadable via a presigned URL.

---

## Contact Profile API

```
GET    /marketing/contacts                        List/search contacts
POST   /marketing/contacts                        Create contact
GET    /marketing/contacts/{id}                   Get contact
PATCH  /marketing/contacts/{id}                   Update fields
DELETE /marketing/contacts/{id}                   Erase (right to delete)
GET    /marketing/contacts/{id}/events            Activity history
GET    /marketing/contacts/{id}/lists             List memberships
POST   /marketing/contacts/{id}/lists/{list-id}   Add to list
DELETE /marketing/contacts/{id}/lists/{list-id}   Remove from list
GET    /marketing/contacts/lookup?email={email}   Find by email
```

---

## Relationship to the Symposia Identity Layer

The `symposia_identity_id` column links a marketer's contact record to the individual's Symposia-level identity profile. This link is optional — not all contacts will have a Symposia identity.

When the link exists:
- The individual can see this marketer in their [User Profile Visibility](../Identity/user-profile-visibility.md) dashboard.
- An erasure request submitted through the individual's Symposia profile will propagate to this marketer's contact record.
- Consent updates made by the individual at the Symposia level (e.g., "block all email from marketing platforms") are respected here.

The link is established when:
- The individual registers for a Symposia account using the same email address as this contact record.
- The tracking system (see [Tracking Architecture](../Tracking/tracking-architecture.md)) matches the browser's Symposia cookie to a contact record for this tenant.
- The individual explicitly claims this contact record through the Symposia profile portal.

The link cannot be established by the marketer — it must be confirmed from the individual's side, or through the trusted tracking system. The marketer cannot inject a fake `symposia_identity_id`.
