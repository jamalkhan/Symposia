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

Contacts belong to one or more **lists** (also called audiences). A list is a static collection of contacts — a snapshot. Segments (dynamic filters) are different from lists (see [Segmentation Engine](./segmentation-engine.md)). Full list-type rules, APIs, and suppression/seed behavior: [Contact Import & Lists](./contact-import-and-lists.md).

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

### Contact Enrichment

Marketers and appbuilders granted the `data_enrichment` permission (see [User Data Ownership](../Identity/user-data-ownership.md#permission-model)) may create derived attributes about an individual — brand affinity scores, propensity models, buyer behavioral profiles, marketing list membership, custom ML scores, and more. These enrichment attributes are stored in the marketer's own Postgres, not in Symposia's central data store.

The `symposia_visible` flag controls whether an attribute is surfaced to the individual through the Symposia profile portal. The platform calls each linked tenant's enrichment API to aggregate visible attributes when the individual views their profile.

**Namespace separation**: If a marketer and Symposia both compute an attribute with the same name (e.g., `brand_affinity`), they are stored and displayed under separate namespaces (`{tenant_id}.brand_affinity` vs. `symposia.brand_affinity`). A marketer's enrichment data never overwrites Symposia's derived attributes. See [Symposia Data Cloud — Namespace Separation](../DataCloud/symposia-data-cloud.md#namespace-separation).

```sql
CREATE TABLE marketing.contact_enrichment (
  enrichment_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id         UUID NOT NULL,
  contact_id        UUID NOT NULL REFERENCES marketing.contacts(contact_id),
  attribute_key     TEXT NOT NULL,                         -- e.g. 'brand_affinity', 'churn_propensity'
  attribute_value   JSONB NOT NULL,                        -- flexible: scalar, array, or object
  owner_type        TEXT NOT NULL DEFAULT 'marketer',      -- 'marketer' | 'appbuilder'
  owner_id          UUID NOT NULL,                         -- tenant_id or appbuilder_id
  symposia_visible  BOOLEAN NOT NULL DEFAULT FALSE,        -- whether individual sees this in profile portal
  created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),

  UNIQUE (tenant_id, contact_id, attribute_key, owner_id)
);

CREATE INDEX ON marketing.contact_enrichment (tenant_id, contact_id);
CREATE INDEX ON marketing.contact_enrichment (tenant_id, attribute_key);
CREATE INDEX ON marketing.contact_enrichment (contact_id) WHERE symposia_visible = TRUE;
```

On a right-to-delete request, enrichment attributes are **anonymized**, not deleted, per the [Erasure and the Created-Data Layer](#erasure-and-the-created-data-layer) rules — they are created/derived-layer data owned by the marketer or appbuilder (`owner_id`). The platform routes the anonymization obligation to the correct `owner_id` based on `owner_type`.

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

## Contact Import & Lists (full spec)

**Authoritative detail** for bulk import jobs, list types (standard / suppression / seed), membership APIs, export, compliance attestation, and erasure-hash behavior at import:

→ **[Contact Import & Lists](./contact-import-and-lists.md)**

The sections below remain a short overview; if they conflict with that document, **contact-import-and-lists.md wins**.

### Import (summary)

1. Upload CSV/JSONL to blob or multipart API → import job.  
2. Validate → field map → compliance attestation if needed → process rows.  
3. Skip erased emails; upsert by `(tenant_id, email)`; optional add to lists.  
4. Summary: created / updated / skipped + error report.

### Single contact API (summary)

```
POST /marketing/contacts
{
  "email": "user@example.com",
  "first_name": "Jamal",
  "compliance": { "email_consent_basis": "express", ... },
  "lists": ["list_id_1"]
}
```

### Export (summary)

Async job → CSV/JSON in tenant blob → presigned download URL; audit logged.

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

---

## Platform Identifier Index

The platform identifier index is a **Symposia-managed, cross-tenant lookup table** that answers "which marketers hold a contact record for this individual" in O(1) time. It is not stored in any marketer's tenant Postgres — it lives in Symposia's internal platform database.

This index powers:
- The individual's profile portal ("brands who have data on me")
- Cross-marketer deletion propagation (right-to-delete dispatched to all affected tenants simultaneously)
- Cross-marketer rectification propagation (identity update events routed to all affected tenants)
- The "claim my records" flow (individual links their Symposia identity to existing marketer contact records)

### Schema

```sql
-- Symposia platform database (NOT tenant Postgres)
CREATE TABLE platform.identity_index (
  index_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  symposia_identity_id  UUID,                    -- null until the individual claims this record
  identifier_type       TEXT NOT NULL,           -- 'email' | 'phone' | 'cookie_id'
  identifier_hash       TEXT NOT NULL,           -- SHA-256(normalize(identifier)); never plaintext
  tenant_id             UUID NOT NULL,           -- which marketer holds this identifier
  contact_id            UUID NOT NULL,           -- the marketer's contact_id (foreign reference, not FK)
  linked_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
  claimed_at            TIMESTAMPTZ,             -- when the individual confirmed this link

  UNIQUE (identifier_hash, tenant_id)
);

-- Primary lookup: "which tenants hold this identifier?"
CREATE INDEX ON platform.identity_index (identifier_hash);

-- Cross-deletion/rectification dispatch: "which index entries link to this Symposia identity?"
CREATE INDEX ON platform.identity_index (symposia_identity_id)
  WHERE symposia_identity_id IS NOT NULL;

-- Tenant-scoped lookup: "which of this tenant's contacts are linked to a Symposia identity?"
CREATE INDEX ON platform.identity_index (tenant_id, symposia_identity_id)
  WHERE symposia_identity_id IS NOT NULL;
```

### How It Is Maintained

| Event | Index Action |
|---|---|
| Contact created via Contact API | Platform writes an entry per identifier (`email`, `phone` if present) |
| Contact email or phone updated | Platform updates the corresponding `identifier_hash` |
| Contact erased (right to delete) | Platform removes the index entry; also removes `symposia_identity_id` link |
| Individual claims a contact record | Platform sets `symposia_identity_id` and `claimed_at` on the matching entry |
| Individual revokes a marketer's access | `symposia_identity_id` is nulled; the marketer's contact record still exists but is no longer linked |

Maintenance is event-driven: the Contact API publishes internal platform events on create/update/delete, and the index is updated synchronously before the API response returns. Index writes are not async — a contact that exists must immediately appear in the index.

### Privacy Design

- **Hashed identifiers only**: the index stores `SHA-256(normalize(email))` — not the email address itself. A lookup requires knowing (or guessing) the plaintext value; the index cannot be scanned to enumerate individuals.
- **No cross-tenant data exposure**: the index records `tenant_id` and `contact_id` only — it does not store what data the marketer holds. One marketer cannot discover another marketer's index entries.
- **Platform-internal only**: the index is not queryable via any marketer-facing API. Marketers cannot enumerate other tenants that hold a record for a given email. Only Symposia's platform services (profile portal, deletion processor, rectification dispatcher) have access.
- **Normalization before hashing**: emails are lowercased and trimmed; phone numbers are normalized to E.164 before hashing, so `Jamal@Gmail.com` and `jamal@gmail.com` hash to the same value.
