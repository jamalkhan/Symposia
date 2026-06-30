# Right to Delete / Right to Erasure

## Overview

Individuals have the right to request that their data be deleted from marketers' systems. This right exists under multiple laws (GDPR Article 17, CCPA, Brazil's LGPD, and an expanding list of state/national privacy laws) and is a first-class feature of the Symposia platform — not an afterthought handled by customer service tickets.

On Symposia, deletion requests can be submitted:
1. **Via a marketer's preference center** — the individual submits a deletion request for that specific marketer's data.
2. **Via the Symposia identity profile** — the individual submits a deletion request that propagates to all marketers on the platform who hold a record linked to their Symposia identity.

---

## What "Deletion" Means

A deletion request does not simply mean removing a database row. The individual has the right to have their data erased from all locations where it is held — including backups, exports, and analytical copies.

On Symposia, the deletion process:

1. **Deletes the contact record** from the marketer's `marketing.contacts` table.
2. **Deletes all linked event history** from `marketing.contact_events` for that contact.
3. **Removes all list memberships** from `marketing.list_memberships`.
4. **Retains a hashed suppression entry** in `marketing.erasure_hashes` (SHA-256 of lowercased email address). This prevents re-import of the same address.
5. **Flags the deletion in the contact database backup** — the next backup rotation will not contain this contact's data. If the contact appears in an existing backup that has not yet expired, the backup is flagged as containing the erased individual (not deleted immediately — backup integrity concerns make immediate backup deletion impractical, but the record is expired at the next rotation window).
6. **Notifies the marketer** via webhook (`contact.erased` event) so they can remove data held in external systems they've synced the contact to.
7. **Records the erasure event** in the compliance audit log and publishes a `compliance.deletion_requested` event to the platform event bus. This event is included in the next hourly Merkle commitment batch and committed to the blockchain — providing a tamper-evident, publicly verifiable record that the deletion request was received at a specific time. See [Event Integrity](../Platform/event-integrity.md).
8. **Does NOT erase email delivery records** that are necessary for legal compliance purposes. Example: the fact that an email was sent on a given date (without the message content or personal details beyond the send timestamp and category) may need to be retained for CAN-SPAM compliance. This limited metadata can be retained; the contact's personal data cannot.

### Verifying a Deletion Request

An individual can verify that their deletion request was recorded and has not been altered since submission. From the Symposia profile portal, they can request a Merkle proof for any `compliance.deletion_requested` event in their history. The proof can be verified independently against the on-chain commitment — no trust in the platform is required.

See [Event Integrity — Individual Verification API](../Platform/event-integrity.md#individual-verification-api) for the proof format and verification steps.

### The Suppression Hash Problem

After erasure, if the marketer re-imports this person's email address in a new contact list, the platform must prevent this from re-enabling sends to someone who has been erased. The erasure hash (SHA-256 of email) retained in `marketing.erasure_hashes` is checked at import time. If a match is found, the row is skipped with a note in the import report: "Previously erased — not re-imported."

This is a deliberate data retention decision: the hash is not the email address (SHA-256 is not reversible for a secret input), so it does not constitute retaining personal data for GDPR purposes, but it effectively blocks re-import. GDPR's "right to erasure" does not prohibit retaining the minimum information needed to honor the right — the suppression hash is that minimum.

---

## Deletion Timeline

GDPR requires erasure "without undue delay" and "within one month" of the request. The platform targets faster than required:

| Step | SLA |
|---|---|
| Deletion request received and acknowledged | Immediate |
| Contact record deleted from primary database | Within 1 hour |
| Contact removed from all active send queues | Within 1 hour (pending sends are cancelled) |
| Event history deleted | Within 24 hours |
| Backup expiry flagged | At next backup rotation (max 7 days, per WAL retention default) |
| Marketer notified via webhook | Within 1 hour of primary deletion |
| Compliance audit log entry | Retained for 5 years (for accountability purposes — the record that a deletion occurred, not the deleted data) |

---

## Network-Level Deletion (Symposia Identity)

When an individual submits a deletion request through their Symposia identity profile, the deletion propagates across all marketers who hold a contact record linked to their `symposia_identity_id`.

Propagation process:
1. The individual submits the deletion request in their Symposia profile.
2. The platform identifies all marketer tenants with a `contacts` row where `symposia_identity_id` matches.
3. A deletion job is queued for each marketer tenant — identical to a single-marketer deletion request.
4. The individual receives a confirmation with the list of marketers being notified and an expected completion time.
5. Each marketer receives a `contact.erased` webhook. The marketer's webhook handler is expected to cascade deletion to any external systems.

If a marketer does not have a webhook configured or the webhook fails, the platform retries for 24 hours before marking the notification as failed. The platform logs the failure but cannot force-delete from external systems the marketer has synced data to — this is disclosed in the ToS and the individual is advised.

### Unlinked Contact Records

If a marketer holds a contact record for an individual who does not have a linked Symposia identity, the individual must submit a deletion request directly through that marketer's preference center. The platform cannot propagate a deletion to a record with no `symposia_identity_id` linkage.

The preference center is always accessible via any past email's `{{ preferences_url }}` link. The individual can also email the marketer's disclosed contact address. The platform's deletion request endpoint does not require a Symposia account:

```
POST https://prefs.symposia.network/{tenant-slug}/delete-request

{
  "email": "individual@example.com",
  "verification_method": "email"   // platform sends a verification email to confirm identity
}
```

---

## Deletion Request API

### Individual-Facing (no auth required, email verification used)

```
POST   /identity/delete-request                   Submit deletion request (email verified)
GET    /identity/delete-request/{request-id}      Check status
```

### Marketer-Facing

```
POST   /marketing/contacts/{id}/erase             Initiate erasure for a contact (marketer-initiated)
GET    /marketing/erasure-requests                List all erasure requests for this tenant
GET    /marketing/erasure-requests/{request-id}   Status of a specific request
```

Marketers can initiate erasure themselves (for example, when a customer calls support to request deletion). The outcome is the same as an individual-initiated request.

---

## Right to Delete vs. Right to Unsubscribe

These are different rights and must not be conflated:

| | Unsubscribe | Right to Erase |
|---|---|---|
| Effect | Stops future sends. Contact record stays. | Removes the contact record and all data. |
| Data retained | Contact row, suppression entry, event history | Suppression hash only |
| Re-subscribable | Yes, with new consent | No — unless the individual initiates contact again |
| Legal basis | CAN-SPAM, CASL, preference | GDPR Art. 17, CCPA, etc. |
| Who can initiate | Individual via unsubscribe link | Individual via deletion form; or marketer |

---

## Vendor and Downstream Propagation

When the platform sends a `contact.erased` webhook to the marketer, the marketer is responsible for:
- Removing the individual's data from any external CRM (Salesforce, HubSpot, etc.) they have synced Symposia data to.
- Removing the individual from any third-party ad platforms they have been matched against.
- Notifying any sub-processors they have shared the data with.

The platform cannot enforce this — it is the marketer's legal obligation as the data controller. The platform's ToS makes this obligation explicit and the platform surfaces the list of integrations the marketer has active so the operator knows where to propagate the deletion.

---

## Open Questions

## Answered Questions
- **Content stored in blob storage**: If the marketer has exported a CSV of contacts to their blob bucket, the deletion does not automatically purge that export. The `contact.erased` webhook notifies the marketer, but blob objects must be deleted by the marketer. Should the platform provide a helper (e.g., scan blob buckets for files matching the erased email)? This is complex and possibly over-reach.
> No. Symposia is subject to the deletion request, and the platform's duty is to notify the marketer of the request. The marketer's operational agreement is such that they are accountable to honor any such deletion requests.

- **Email archive retention**: If the marketer uses a third-party email archive service (for compliance with financial regulations, for example), the archived emails may contain the individual's address. Is the platform responsible for propagating deletion to the archive, or does this remain the marketer's problem?
> No. Symposia is subject to the deletion request, and the platform's duty is to notify the marketer of the request. The marketer's operational agreement is such that they are accountable to honor any such deletion requests.
> Additionally, this platform *may* be used for transactional sends, which have different scrutiny than promotional sends. Deletion requests are still honored for marketing purposes.
