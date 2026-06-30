# Email Compliance

## Overview

Marketing email is regulated in every major market. The platform must enforce the rules marketers are bound by — not as optional best practices but as technical constraints on what the platform allows. Compliance is enforced at send time, not left to the marketer's discretion.

This document covers the major email-specific regulations: CAN-SPAM (US), CASL (Canada), GDPR email provisions (EU/UK), PECR (UK), and LGPD (Brazil). GDPR's broader data rights (right to delete, right to access) are covered in [Right to Delete](../Identity/right-to-delete.md) and [User Data Ownership](../Identity/user-data-ownership.md).

---

## CAN-SPAM Act (United States)

The CAN-SPAM Act applies to any commercial email message sent to US recipients. Unlike GDPR, it does not require prior consent — but it strictly governs what the email must contain and what the sender must do when someone unsubscribes.

### Requirements Enforced by the Platform

| Requirement | How the Platform Enforces It |
|---|---|
| Accurate `From:`, `Reply-To:`, and routing information | Verified sending domains only. `From:` must match the verified sending domain. |
| Non-deceptive subject line | Cannot be technically enforced, but deceptive subjects are an AUP violation triggering account review. |
| Clear identification as an advertisement | If the message is classified as `marketing`, the platform injects a marker in the message metadata. Tenants must disclose ad status in body copy — platform cannot enforce body copy compliance but can surface a checklist before send. |
| Physical postal address | Required field on each sending domain configuration. Must be a real postal address. Injected into footer template if `{{ sender.address }}` is used. The platform blocks sends from sending domains with no postal address configured. |
| Clear and conspicuous unsubscribe mechanism | `List-Unsubscribe` header injected automatically. Platform verifies that the rendered HTML body contains at least one instance of `{{ unsubscribe_url }}` or equivalent; blocks send if not present. |
| Honor unsubscribes within 10 business days | One-click unsubscribes take effect immediately (within seconds). Manual unsubscribes via the preference center take effect immediately. The 10-day window is a legal maximum; the platform does not use it. |
| No selling/transferring of opt-out lists | Platform policy. Covered in [Terms of Service](../Legal/terms-of-service-and-acceptable-use.md). |

### Third-Party Mailers

If a marketer uses the platform to send on behalf of another company (e.g., an agency sending for a client), both parties are liable under CAN-SPAM. The platform's ToS requires the initiating marketer to ensure compliance. The platform collects the company name and postal address per sending domain — this is the disclosed sender under CAN-SPAM, and it must be accurate.

---

## CASL (Canada's Anti-Spam Legislation)

CASL is one of the strictest email laws globally. Unlike CAN-SPAM, **CASL requires prior express or implied consent before sending a commercial electronic message (CEM) to a Canadian recipient**. The burden of proof for consent lies with the sender.

### Consent Types

| Type | Description | Validity |
|---|---|---|
| **Express consent** | Recipient explicitly opted in (checkbox, sign-up form). The opt-in copy must name the sender and describe the message type. | Valid indefinitely until withdrawn. |
| **Implied consent** | A business relationship exists: the recipient purchased from or inquired with the sender within the past 2 years (for customers) or 6 months (for inquiries/applications). | Expires after 2 years / 6 months from the last qualifying interaction. |

### Platform Requirements for CASL

- Marketers must record the **consent basis** for every contact at import or capture time: `express` or `implied`.
- For `implied` consent, marketers must record the **consent date** — the date of the last qualifying interaction.
- The contact database enforces that `implied_consent_expires_at` is calculated and stored (e.g., 2 years from `last_purchase_date`).
- At send time, for contacts flagged as Canadian (by country field or inferred from email domain patterns where possible), the pre-send processor:
  1. Verifies `consent_basis` is set.
  2. Verifies `implied_consent_expires_at` has not passed.
  3. Skips the contact (marks as `compliance_skip`) if consent is missing or expired — does not block the send for all recipients.
- Compliance skips are surfaced in the delivery report.

### CASL Unsubscribe

CASL requires that unsubscribe mechanisms work for at least 60 days after the message is sent. The platform retains unsubscribe link tokens for a minimum of 90 days.

---

## GDPR — Email Marketing Provisions (EU and UK)

GDPR's lawful basis for marketing email is typically **consent** (Article 6(1)(a)) or **legitimate interests** (Article 6(1)(f)). For the marketing use case, consent is strongly preferred — legitimate interests for direct marketing to individuals is contested in many EU member states.

### Consent Requirements for GDPR Marketing Email

Consent under GDPR must be:
- **Freely given**: No pre-ticked boxes. Consent must not be bundled with terms acceptance.
- **Specific**: Consent to receive marketing email specifically (not general terms consent).
- **Informed**: The contact must know who they are consenting to hear from and what type of content.
- **Unambiguous**: A clear affirmative action (not silence or inactivity).

### Platform Requirements for GDPR

- Marketers must record the **consent date**, **consent source** (e.g., "sign-up form on /checkout", "in-store tablet"), and **consent wording** (the exact text the person agreed to) for each EU contact.
- The contact database stores `gdpr_consent_recorded_at`, `gdpr_consent_source`, `gdpr_consent_wording`.
- Marketers cannot import an EU contact list without attaching a consent record to each contact. The platform enforces this at import time (rejects rows without consent metadata for EU contacts).
- The right to withdraw consent is the unsubscribe mechanism — same one-click mechanism used for CAN-SPAM, with immediate effect.
- The right to erasure (right to be forgotten) removes the contact record. See [Right to Delete](../Identity/right-to-delete.md).
- Marketers must be able to export consent records on request from a supervisory authority.

### EU Contact Detection

The platform infers EU/UK/Canada jurisdiction based on the contact's `country` field. If `country` is not set, more permissive rules apply by default, but the system should warn the marketer. A "strict mode" setting enables GDPR-level consent requirements for all contacts regardless of country (recommended for any marketer with a significant EU audience).

---

## PECR (UK — Privacy and Electronic Communications Regulations)

PECR applies to electronic marketing communications in the UK and supplements the UK GDPR. For email specifically:
- B2C (individual consumers): requires **prior explicit consent**.
- B2B (emails to corporate addresses like `sales@company.com`): permits **soft opt-in** — the sender has an existing business relationship and the message is for similar products/services.

The contact database includes a `contact_type` field (`consumer` | `business`). B2B contacts with a relationship flag may be emailed under PECR soft opt-in. The consent recording requirements above apply to B2C UK contacts identically to GDPR EU contacts.

---

## Consent Recording in the Contact Database

Every contact in the marketer's database must have a compliance record attached. The compliance record is a structured field on the contact, not a separate table, so it travels with the contact on export and import:

```json
{
  "contact_id": "con_01abc",
  "email": "recipient@example.com",
  "compliance": {
    "email_consent_basis": "express",
    "email_consent_recorded_at": "2026-03-15T10:23:00Z",
    "email_consent_source": "checkout_opt_in",
    "email_consent_wording": "I agree to receive marketing emails from Malamute Adventures.",
    "jurisdiction": "EU",
    "implied_consent_expires_at": null,
    "can_spam_postal_address_on_record": true,
    "casl_last_qualifying_interaction_at": null
  }
}
```

Marketers who import contacts without compliance records receive a warning and must acknowledge a compliance attestation: "I confirm that I have lawful basis to send marketing email to these contacts under applicable law."

---

## Pre-Send Compliance Check

Before any message leaves the queue, the pre-send processor runs a compliance gate:

| Check | Fail Action |
|---|---|
| Recipient is on suppression list | Skip recipient. Log `suppressed`. |
| Recipient has unsubscribed | Skip. Log `unsubscribed`. |
| CASL implied consent expired | Skip. Log `compliance_skip: casl_expired`. |
| No consent record and strict mode enabled | Skip. Log `compliance_skip: no_consent`. |
| Sending domain has no postal address | Block entire send. Error returned to marketer. |
| HTML body has no unsubscribe link | Block entire send. Error with template line hint. |
| Message classified as marketing but missing sender identification | Warning logged; send proceeds with platform-injected disclosure header. |

Skipped recipients are shown in the send report with their skip reason. Blocked sends require the marketer to fix the configuration before retrying.

---

## Subscription Management for Recipients

Recipients (non-marketer individuals) manage their subscriptions through two surfaces:

1. **One-click unsubscribe** (in email header): processed by the platform automatically, no web page visit required.
2. **Preference center** (linked from `{{ preferences_url }}` in email body): a platform-hosted web page where recipients can:
   - Unsubscribe from all email from this sender.
   - Unsubscribe from specific list/category (if the marketer has configured list categories).
   - Update their email address.
   - Request data deletion (right to erase).

See [Subscription Management](../Identity/subscription-management.md) for the individual's broader rights across multiple marketers.

---

## Audit Log

All compliance-relevant events are logged and retained for 5 years minimum (EU Article 5(2) accountability requirement):

- Consent recorded / source / wording
- Unsubscribe received / method / timestamp
- Suppression added / reason
- GDPR deletion request received / fulfilled / timestamp
- Compliance skips at send time with reason

Marketers can export the audit log via API:
```
GET /marketing/contacts/{contact-id}/compliance-log
GET /marketing/compliance/audit?from={date}&to={date}    (account-wide)
```

---

## Open Questions

- **CASL enforcement for unknown country**: If a contact has no country set, should the platform apply the most permissive rules (CAN-SPAM) or the strictest (CASL)? Defaulting to CAN-SPAM protects the platform from liability for the marketer's data quality, but CASL default would be more conservative. Leaning toward a "warn but allow" approach with a dashboard nudge to set country on contacts.
- **Legitimate interests for B2B**: Some B2B marketers will want to use legitimate interests as the GDPR basis for emailing business contacts. Does the platform support this? It changes what needs to be recorded in the compliance fields.
- **LGPD (Brazil) and other emerging laws**: Brazil's LGPD, India's DPDP Act, and many others have similar but distinct consent requirements. Does the platform try to model each jurisdiction, or enforce GDPR-level requirements universally as a safe floor?
