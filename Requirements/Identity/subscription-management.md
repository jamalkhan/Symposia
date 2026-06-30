# Subscription Management

## Overview

Individuals must be able to manage their own email subscription status without requiring any action by the marketer. This covers: one-click unsubscribe from a specific marketer, category-level preferences (receive promotions but not newsletters), and a global "no marketing email from any Symposia marketer" option.

Subscription management is handled at two levels:

1. **Marketer-level**: individual manages their subscription with a specific marketer through that marketer's preference center.
2. **Symposia-level**: individual manages their preferences across all marketers through their Symposia identity profile.

---

## Unsubscribe Mechanisms

### 1. One-Click Unsubscribe (RFC 8058)

Every marketing email sent through the platform includes:
```
List-Unsubscribe: <https://track.symposia.network/unsub/{token}>, <mailto:unsub-{token}@unsub.symposia.network>
List-Unsubscribe-Post: List-Unsubscribe=One-Click
```

Gmail and Outlook show an "Unsubscribe" button in the email UI that triggers an HTTP POST to the URL. The platform processes this immediately:
1. Marks the contact's `email_status` as `unsubscribed` in the marketer's contact database.
2. Adds the address to the marketer's suppression list.
3. Records the unsubscribe event in the contact's activity history and publishes a `compliance.unsubscribe_requested` event to the platform event bus. This event is included in the next hourly Merkle commitment batch and committed to the blockchain — providing a tamper-evident record that the unsubscribe was received at a specific time.
4. If the contact has a Symposia identity link, records the marketer-level unsubscribe in the individual's permission record.

Individuals can verify their unsubscribe was recorded and has not been altered by requesting a Merkle proof from the profile portal. See [Event Integrity — Individual Verification API](../Platform/event-integrity.md#individual-verification-api).

Tokens are valid for 90 days after the send date. After expiry, the token still resolves to an unsubscribe page where the individual can submit their email address manually.

### 2. Unsubscribe Link in Email Body

Every marketing email body must include a visible unsubscribe link using `{{ unsubscribe_url }}`. Clicking this link takes the individual to the preference center (see below) where they can confirm unsubscribe or manage their preferences.

### 3. Preference Center

The preference center is a platform-hosted web page, branded to the marketer (marketer logo and name, neutral platform UI). It is reachable via `{{ preferences_url }}` in the email.

Capabilities in the preference center:

| Option | Description |
|---|---|
| **Unsubscribe from all** | Stop all marketing email from this marketer. Effective immediately. |
| **Category preferences** | If the marketer has configured email categories (e.g., "Newsletters", "Promotions", "Product Updates"), the individual can opt out of specific categories while staying on others. |
| **Frequency preference** | If the marketer has configured frequency options (e.g., "Daily", "Weekly", "Monthly"), the individual can downgrade their frequency rather than unsubscribing entirely. |
| **Update email address** | Change the email address associated with this contact record. The old address is suppressed; the new address requires a confirmation email before being activated. |
| **Delete my data** | Triggers a right-to-delete request. Redirects to the deletion flow (see [Right to Delete](./right-to-delete.md)). |

Preference center URL format: `https://prefs.symposia.network/{tenant-slug}/{contact-token}`

The `contact-token` is HMAC-signed with the tenant's key, encodes the contact ID, and is valid for 90 days. The preference center does not require login — the token authenticates the individual.

---

## Global Unsubscribe (Symposia-Level)

An individual with a Symposia identity can set a global marketing preference in their Symposia profile:

- **"Do not send me marketing email from any Symposia marketer"**: When this is set, the platform suppresses all marketing sends to this individual across all tenants, regardless of list membership or consent records in those tenants' databases. The individual is still reachable for transactional email.

This global preference is enforced at the pre-send compliance check — it is a platform-level override that no marketer can circumvent.

How it works technically:
- The individual's Symposia identity record contains `global_marketing_opt_out: true`.
- The pre-send processor looks up the `symposia_identity_id` on each contact record before sending.
- If the linked identity has `global_marketing_opt_out: true`, the message is skipped with status `compliance_skip: global_opt_out`.

The marketer sees `compliance_skip: global_opt_out` in their delivery report but does not see the individual's Symposia identity details. They know not to send; they do not know why the individual set this preference.

---

## Category-Level Subscriptions

Marketers may configure up to 10 named email categories per tenant (e.g., "Product Updates", "Promotions", "Monthly Newsletter", "Event Invitations"). Each category maps to a tag on the contact record.

When a contact unsubscribes from a category, that category's tag is removed (or a `do_not_send` tag is added, depending on the model). Campaign sends can target "all subscribed contacts who have not opted out of category X."

Category management API:
```
GET    /marketing/email-categories                     List categories
POST   /marketing/email-categories                     Create category
PUT    /marketing/email-categories/{id}                Update
DELETE /marketing/email-categories/{id}                Delete
GET    /marketing/contacts/{id}/category-preferences   Get contact's preferences
PATCH  /marketing/contacts/{id}/category-preferences   Update preferences
```

---

## Subscription Status Transitions

```
Imported / Created
        │
        ▼
  [subscribed] ──────────────────────────────────┐
        │                                         │
        │ unsubscribes (any mechanism)            │ hard bounce
        ▼                                         ▼
  [unsubscribed]                            [bounced]
        │                                         │
        │ re-subscribes (consent re-collected)    │ (no automatic recovery)
        ▼
  [subscribed]

  [complained] ← FBL complaint received (no recovery without manual review)
  [deleted]    ← right-to-delete fulfilled (suppression hash retained)
```

Re-subscribing a previously unsubscribed contact requires a new consent event. The platform logs the new consent before allowing the `email_status` to return to `subscribed`. Marketers cannot re-subscribe a contact programmatically without supplying a consent record.

---

## API

```
POST /marketing/contacts/{id}/unsubscribe           Unsubscribe a contact (marketer-initiated or via webhook)
POST /marketing/contacts/{id}/resubscribe           Re-subscribe with new consent
GET  /marketing/contacts/{id}/subscription-status   Current status and history
POST /marketing/unsubscribe/{token}                 Process one-click token (called by email client or preference center)
```
