# Contact Import & Lists

## Overview

This document is the **MVP-authoritative** spec for:

1. **Bulk and API contact import** (getting people into the system legally and safely)  
2. **List management** (static audiences: standard, suppression, seed, and related ops)

Schema foundations live in [Contact Database](./contact-database.md). Segmentation (dynamic audiences) is [Segmentation Engine](./segmentation-engine.md). Campaigns consume lists/segments per [Campaigns](../Messaging/campaigns.md). Compliance rules align with [Email Compliance](../Messaging/email-compliance.md) and [Right to Delete](../Identity/right-to-delete.md).

**MVP success:** a marketer can CSV-import contacts with consent handling, put them on lists, exclude suppression, and target a Broadcast — without re-importing erased people.

---

## Lists vs segments (recap)

| | **List** | **Segment** |
|---|---|---|
| Membership | Explicit add/remove | Computed from filters |
| Changes | Only when you change it | Auto as data changes |
| Use | Imports, static audiences, seeds, suppression | Behavioral / property targeting |
| Send-time cost | Cheap join | Query or pre-materialized membership |

Campaigns may target **lists**, **segments**, or both (includes + excludes). See Campaigns audience config.

---

## List types

| `list_type` | Purpose | Who adds members |
|---|---|---|
| **`standard`** | Marketing audiences (newsletter, VIP, event attendees) | Import, API, UI, Journey actions |
| **`suppression`** | Never send marketing to these addresses | Platform (bounce, complaint, unsub, global opt-out) + marketer manual |
| **`seed`** | QA recipients who always get a copy of sends | Marketer only |
| **`system`** | Platform-managed helpers (optional; e.g. “pending double opt-in”) | Platform |

### Rules by type

#### Standard

- Unlimited per tenant (soft abuse caps may apply).  
- May be used as Campaign include/exclude.  
- Membership does **not** imply consent — consent lives on the contact record.  
- Deleting a list removes memberships only; contacts remain.

#### Suppression

- **Exactly one** active suppression list per tenant (`list_type = suppression`, `is_primary = true`).  
- Auto-enrolled when:
  - Hard bounce  
  - FBL / complaint  
  - Marketing unsubscribe (list or global for that marketer)  
  - Platform `global_marketing_opt_out` for linked identity (all tenants’ suppression as applicable)  
  - Manual marketer add  
- **Pre-send:** marketing messages skip if email is on suppression **or** `email_status` ∈ (`unsubscribed`, `bounced`, `complained`, `deleted`) **or** erasure hash match. Defense in depth.  
- Transactional sends: still honor erasure + hard bounce/complaint suppression; marketing unsub alone does not block transactional (per [Campaigns / email compliance](../Messaging/email-compliance.md)).  
- Contacts on suppression may still exist as rows (for audit) with `email_status` updated.

#### Seed

- Not counted in campaign performance denominators by default (Campaigns analytics).  
- Still require valid email; still blocked if on suppression or erased.  
- Max seed list size: **100** addresses per tenant (MVP).  
- Must not be used as a primary marketing audience.

---

## List data model

```sql
CREATE TABLE marketing.lists (
  list_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        UUID NOT NULL,
  name             TEXT NOT NULL,
  description      TEXT,
  list_type        TEXT NOT NULL DEFAULT 'standard',
                   -- standard | suppression | seed | system
  is_primary       BOOLEAN NOT NULL DEFAULT FALSE,
                   -- true only for the one primary suppression list
  status           TEXT NOT NULL DEFAULT 'active',  -- active | archived
  created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
  created_by       UUID,
  contact_count    INT NOT NULL DEFAULT 0,
  metadata         JSONB NOT NULL DEFAULT '{}',

  UNIQUE (tenant_id, name),
  CONSTRAINT one_primary_suppression_per_tenant
    EXCLUDE (tenant_id WITH =)
    WHERE (list_type = 'suppression' AND is_primary AND status = 'active')
  -- implement as partial unique index if EXCLUDE unavailable:
  -- UNIQUE (tenant_id) WHERE list_type = 'suppression' AND is_primary AND status = 'active'
);

CREATE TABLE marketing.list_memberships (
  list_id          UUID NOT NULL REFERENCES marketing.lists(list_id) ON DELETE CASCADE,
  contact_id       UUID NOT NULL REFERENCES marketing.contacts(contact_id) ON DELETE CASCADE,
  tenant_id        UUID NOT NULL,
  subscribed_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  source           TEXT,              -- import | api | ui | journey | system | suppression_auto
  source_detail    TEXT,              -- import_job_id, journey_id, etc.
  PRIMARY KEY (list_id, contact_id)
);

CREATE INDEX ON marketing.list_memberships (tenant_id, contact_id);
CREATE INDEX ON marketing.list_memberships (list_id) INCLUDE (contact_id);
```

**On tenant create:** platform creates primary suppression list + empty default seed list (optional) + no standard lists until marketer creates them.

**contact_count:** updated transactionally on membership change (or async reconcile job nightly).

### Membership events (NATS + contact_events when useful)

| Event | When |
|---|---|
| `list.created` / `list.archived` | List lifecycle |
| `list.member_added` / `list.member_removed` | Membership change |
| Journey triggers | `added_to_list` / `removed_from_list` per [Journeys](../Journeys/journeys.md) |

Subjects: `sym.{tenant_id}.list.*`

---

## List API

```
GET    /marketing/lists
POST   /marketing/lists
GET    /marketing/lists/{id}
PATCH  /marketing/lists/{id}
POST   /marketing/lists/{id}/archive

GET    /marketing/lists/{id}/members?cursor=&limit=
POST   /marketing/lists/{id}/members              # { "contact_ids": [...] } or { "emails": [...] }
DELETE /marketing/lists/{id}/members              # body: contact_ids or emails
POST   /marketing/lists/{id}/members/import       # attach to import job targeting this list

GET    /marketing/contacts/{id}/lists
POST   /marketing/contacts/{id}/lists/{list_id}
DELETE /marketing/contacts/{id}/lists/{list_id}
```

**Bulk add by email:** resolve or create-stub? MVP: **resolve existing only** for membership add without full contact payload; unknown emails return `not_found` count unless `create_if_missing` with required consent fields (same as import row).

**Cannot** delete the primary suppression list; can clear manual members only with caution (system members re-added by pipeline).

---

## Contact import

### Channels

| Channel | MVP | Notes |
|---|---|---|
| **CSV / TSV** file | **Yes** | Primary bulk path |
| **JSON lines** | Yes | Same job pipeline |
| **Single / batch API** | Yes | `POST /contacts`, `POST /contacts/batch` (≤1000/request) |
| **Blob drop** | Yes | Upload to tenant bucket prefix; same as catalog feeds pattern |
| **SFTP** | Optional | Maps to blob; same processor |
| **ETL connectors** | Post-MVP | Shopify etc. |

### Import job lifecycle

```
draft → validating → awaiting_mapping → awaiting_attestation → queued
  → running → completed | failed | cancelled
```

| State | Meaning |
|---|---|
| `validating` | Encoding, size, virus/malware scan if applicable, sample parse |
| `awaiting_mapping` | Need column → field map (or use saved map) |
| `awaiting_attestation` | Compliance gaps; marketer must attest or fix file |
| `queued` / `running` | Worker processing rows |
| `completed` | Terminal; summary available |
| `failed` | Unrecoverable (corrupt file, abort) |
| `cancelled` | User cancelled; partial commits kept (see below) |

**Partial commits:** MVP uses **streaming upsert** — rows committed as processed. Cancel stops further rows; already-written contacts remain. Job summary shows partial stats. (Full transactional all-or-nothing is not MVP.)

### Job data model

```sql
CREATE TABLE marketing.import_jobs (
  job_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           UUID NOT NULL,
  status              TEXT NOT NULL,
  source_type         TEXT NOT NULL,   -- csv | jsonl | api_batch | blob
  source_uri          TEXT,            -- blob path
  file_name           TEXT,
  file_bytes          BIGINT,
  field_map           JSONB,           -- column → contact field
  options             JSONB NOT NULL DEFAULT '{}',
  /*
    options:
      update_existing: true,
      blank_overwrite: false,
      add_to_list_ids: [uuid],
      default_source: "import",
      default_source_detail: "spring_list_2026",
      strict_consent: false | true,   -- reject rows missing consent for regulated jurisdictions
      dry_run: false
  */
  attestation         JSONB,           -- { text, accepted_at, accepted_by }
  stats               JSONB NOT NULL DEFAULT '{}',
  /*
    stats:
      total_rows, processed, created, updated, skipped_invalid_email,
      skipped_erased, skipped_suppressed_optional, skipped_duplicate_in_file,
      errors_sample: [...]
  */
  error_report_uri    TEXT,            -- blob CSV of row errors
  created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  started_at          TIMESTAMPTZ,
  completed_at        TIMESTAMPTZ,
  created_by          UUID
);
```

### File requirements (CSV)

| Rule | MVP |
|---|---|
| Encoding | UTF-8 (BOM optional); reject with clear error otherwise |
| Max file size | **500 MB** or **2M rows** (whichever first) |
| Required column | **email** (header names case-insensitive; aliases: `Email`, `Email Address`) |
| Delimiter | Auto-detect `,` / `\t` / `;` |
| Quotes | RFC 4180-style |

### Field mapping

Default auto-map for common headers:

| Header examples | Field |
|---|---|
| email, e-mail | `email` |
| first_name, firstname, first | `first_name` |
| last_name, lastname, last | `last_name` |
| phone, mobile | `phone` |
| country, country_code | `country` |
| consent_basis, email_consent_basis | `email_consent_basis` |
| consent_at, email_consent_recorded_at | `email_consent_recorded_at` |
| consent_source | `email_consent_source` |
| consent_wording | `email_consent_wording` |

Unmapped columns → `properties.<sanitized_key>` if `map_unknown_to_properties: true` (default **true**).

Saved maps: `POST /marketing/import-field-maps` for reuse.

### Per-row processing order

For each data row:

1. **Normalize email** — trim, lowercase; validate format (HTML-spec-ish); invalid → skip `invalid_email`.  
2. **Erasure hash check** — `SHA-256(email)` in `marketing.erasure_hashes` → skip `erased` (**never create/update**).  
3. **Optional:** if `skip_if_suppressed` and on suppression list → skip (default **false** for import of historical data; still won’t *send* later).  
4. **Duplicate in file** — same email twice: last row wins (or first wins — **MVP: last wins**); count extras as `duplicate_in_file`.  
5. **Upsert contact** by `(tenant_id, email)`:
   - **Create** if missing  
   - **Update** if `update_existing` (default true): only non-blank incoming fields; blank does not clear unless `blank_overwrite: true`  
6. **Compliance fields:** never clear existing consent with blank import cells. Overwrite only if import provides non-empty consent fields.  
7. **Strict mode / jurisdiction:** if `country` ∈ EU/UK/CA (or tenant `strict_consent: true` globally) and row lacks required consent metadata → skip `missing_consent` **or** hold job for attestation (see below).  
8. **List membership:** add to `add_to_list_ids` if set.  
9. **Platform identifier index:** write/update email (and phone if present) hashes per [contact-database](./contact-database.md#platform-identifier-index).  
10. **Events:** `contact.created` or `contact.updated`; source `import`.  

### Compliance attestation

When any rows lack consent for regulated jurisdictions (or entire file has no consent columns):

1. Job pauses at `awaiting_attestation`.  
2. UI shows count of affected rows + sample.  
3. Marketer must accept attestation text, e.g.:

> I confirm I have a lawful basis to store and send marketing email to these contacts under applicable law (including CAN-SPAM, CASL, and GDPR where applicable). I understand Symposia may suspend sending for abuse or missing consent.

4. Stored with `accepted_by`, `accepted_at`, IP/user agent if available.  
5. Job continues; rows still missing hard-required fields under `strict_consent` remain skipped.

**Purchased/broker lists:** not forbidden by protocol alone, but AUP + first-party claim rules mean they won’t auto-link on identity claim; import `source` should be set honestly (`import_purchased` vs `import_first_party`) for trust features.

### Dry run

`dry_run: true` runs validation + stats without writes. Required for files **>100k rows** before real run? **MVP: strongly recommended in UI, not forced.**

### Import API

```
POST   /marketing/imports                    # multipart file or { blob_uri }
GET    /marketing/imports
GET    /marketing/imports/{job_id}
POST   /marketing/imports/{job_id}/mapping   # set field_map
POST   /marketing/imports/{job_id}/attest
POST   /marketing/imports/{job_id}/start
POST   /marketing/imports/{job_id}/cancel
GET    /marketing/imports/{job_id}/errors    # presigned URL to error report

POST   /marketing/contacts/batch
{
  "contacts": [ { "email": "...", "first_name": "...", "compliance": {...}, "lists": ["..."] } ],
  "options": { "update_existing": true }
}
```

Batch response: per-item status array + summary (created/updated/skipped).

### Performance targets (MVP)

| Scale | Target |
|---|---|
| 10k rows | &lt; 1 minute |
| 100k rows | &lt; 10 minutes |
| 1M rows | &lt; 2 hours |
| Parallelism | Per-tenant serial jobs by default (one running import); queue others |

Workers are OLTP-friendly (batched upserts, avoid full-table locks).

---

## Contact export (MVP)

| Parameter | MVP |
|---|---|
| Formats | CSV, JSON |
| Trigger | Async job → blob → presigned URL |
| Filters | list_id, segment_id, email_status, date range |
| Fields | Allowlist of contact columns + property keys |
| Max rows | 2M or stream to multi-part blob |
| Retention of export blob | 7 days then delete |
| Audit | Log who exported what (compliance) |

Does **not** bypass RBAC (when RBAC exists); MVP single-admin is fine.

```
POST /marketing/contacts/export
GET  /marketing/exports/{id}
```

---

## Import → list → campaign path (MVP happy path)

1. Create list `Newsletter 2026` (`standard`).  
2. `POST /marketing/imports` with CSV + `add_to_list_ids: [newsletter]`.  
3. Map fields; attest if needed; start job.  
4. Job completes: N created, M updated, K skipped erased.  
5. Broadcast Campaign targets that list (+ exclusion segment optional).  
6. Pre-send still checks suppression, consent, frequency caps.

---

## Security & abuse

| Control | MVP |
|---|---|
| Auth | Tenant credentials only; no cross-tenant |
| File scan | Reject executables; size limits |
| Rate | Max concurrent imports = 1; max 20 jobs/day soft |
| PII in logs | No full email in application logs; hash or last-4 domain ok |
| Error report | Contains emails of failed rows — treat as PII; same retention as export |

---

## Relationship to other systems

| System | Interaction |
|---|---|
| **Campaigns** | Include/exclude lists; seed list always CCed |
| **Journeys** | Actions add/remove list; triggers on membership |
| **Email delivery** | Suppression list + status at pre-send |
| **Right to delete** | Erasure hash blocks re-import; remove from all lists on erase |
| **Identifier index** | Updated on import create/update |
| **Double opt-in** | Import may set `email_status = pending` if option set; DOI Journey confirms |

---

## MVP checklist

- [ ] Primary suppression list auto-created per tenant  
- [ ] Standard list CRUD + membership API  
- [ ] Seed list max 100  
- [ ] CSV import job with mapping, attestation, erasure skip, upsert  
- [ ] Batch API ≤1000  
- [ ] Add imported contacts to list in same job  
- [ ] Export async CSV  
- [ ] Error report download  
- [ ] Import does not resurrect erased emails  

---

## Out of scope (post-MVP)

- Bidirectional CRM sync  
- Deduplicate across phone+email graph at import  
- Real-time streaming import (Kafka)  
- Multi-GB single-file without chunking UX  
- Marketer-defined multiple suppression lists with priority rules  

---

## References

- [contact-database.md](./contact-database.md)  
- [segmentation-engine.md](./segmentation-engine.md)  
- [email-compliance.md](../Messaging/email-compliance.md)  
- [outbound-email-delivery.md](../Messaging/outbound-email-delivery.md)  
- [campaigns.md](../Messaging/campaigns.md)  
- [MVP.md](../MVP.md)  
