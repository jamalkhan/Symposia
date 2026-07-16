# Use Case: Double Opt-In (E2E)

**Status: Specced (MVP P0 companion to welcome)**  
**Template key:** `double_opt_in`

## Goal

Confirm that a contact **controls the email address** and **wants marketing** before any promotional send. Required for strong GDPR/express-consent markets; best practice everywhere.

Creates a verifiable consent record (`compliance.consent_granted`) suitable for Merkle commitment.

---

## When DOI is required

| List / capture path | Default |
|---|---|
| List with `double_opt_in_required = true` | Always run DOI before marketing |
| List in EU/UK jurisdiction default | Platform recommends **true** at list create |
| US-only casual list | Optional; marketer chooses |
| Import with `email_status = pending` | Enroll DOI if list requires DOI |
| Transactional Critical/High | **Not** gated by DOI (different category) |

**Relationship to welcome:** Welcome series **must not** send marketing emails until `email_status = subscribed`. If DOI is on, **DOI completes first**, then optionally auto-enrolls Welcome.

---

## Contact states

| `email_status` | Meaning | Marketing sends? |
|---|---|---|
| `pending` | Awaiting confirmation click | **No** |
| `subscribed` | Confirmed (or DOI not required + lawful basis) | Yes (if other gates pass) |
| `unconfirmed` | DOI window expired without click | **No** |
| `unsubscribed` / `bounced` / `complained` | Standard blocks | **No** |

Pre-send always requires `subscribed` for marketing category.

---

## Detection / trigger

| Source | Behavior |
|---|---|
| `contact.created` with `email_status = pending` | Enroll DOI Campaign if any linked list requires DOI **or** contact flag `requires_doi` |
| `added_to_list` on DOI-required list | If contact not yet `subscribed`, set `pending` if needed and enroll |
| `form_submit` / API signup | Create contact as `pending` when DOI required on target list |
| Import option `force_pending: true` | Rows enter as pending; batch enroll DOI |

**Re-entry:** `re_entry_if_not_active` — if already in DOI Journey, don’t double-enroll; if `unconfirmed`, allow re-enroll after marketer resubscribe attempt or new form submit.

---

## Confirmation token

| Property | MVP |
|---|---|
| Format | Opaque URL-safe token (HMAC or random 32+ bytes) |
| Bound to | `tenant_id`, `contact_id`, `enrollment_id`, optional `list_id` |
| TTL | **7 days** (matches Journey wait) |
| Single use | Yes |
| Delivery | Link in confirmation email: `https://prefs.symposia.network/confirm/{token}` (or tenant CNAME) |

### Confirm endpoint

```
GET  /marketing/public/doi/confirm/{token}     # browser click → HTML success
POST /marketing/public/doi/confirm/{token}     # optional API
```

On valid token:

1. Set `email_status = subscribed`  
2. Set/update compliance: `email_consent_basis = express`, `email_consent_recorded_at = now()`, source = `double_opt_in`, wording = versioned template copy  
3. Emit **`compliance.consent_granted`** (NATS + contact_events; **Merkle-committed**)  
4. Emit `contact.updated`  
5. Mark DOI enrollment exit `completed` / confirmed  
6. If configured: **enroll Welcome series** Campaign  
7. Show branded success page  

On expired/invalid: friendly error; optional “resend confirmation” if still pending.

---

## Journey graph (default)

```
[Trigger: pending contact / DOI list add]
        │
        ▼
[Action: Send email — confirm subscription]
        │  transactional-lean: still may use category marketing with unsub;
        │  MVP: category = marketing but subject is confirmation; unsub optional
        │  on pure DOI mail (CAN-SPAM still wants address)
        ▼
[Wait: up to 7 days]
        │  exit condition: doi_confirmed event / enrollment flag from confirm endpoint
        │
        ├─ confirmed ──► [Action: optional enroll Welcome] ──► [Exit completed]
        │
        └─ timeout ──► [Action: set email_status = unconfirmed] ──► [Exit expired]
```

**Do not** send Welcome content inside DOI wait.

### Resend confirmation

Marketer or contact may request resend:

```
POST /marketing/contacts/{id}/doi/resend
```

Invalidates previous token; new email; resets wait window **or** extends once (MVP: **new 7-day window**, max **3** resends).

---

## Campaign config

| Field | Value |
|---|---|
| type | `triggered` / `journey_backed` |
| category | `marketing` (or future `transactional` if product treats DOI as transactional — **MVP: marketing** with light creative rules) |
| priority | `marketing` (not Critical) |
| re_entry | `re_entry_if_not_active` |
| Intent screen | Low risk; still subject to abuse tier caps |

---

## List configuration

```json
{
  "list_id": "uuid",
  "name": "Newsletter",
  "double_opt_in_required": true,
  "welcome_campaign_id": "uuid",
  "jurisdiction_default": "EU"
}
```

On list create in EU-preset: `double_opt_in_required` default **true**.

---

## Compliance & integrity

| Artifact | Committed? |
|---|---|
| `compliance.consent_granted` | **Yes** (hourly Merkle) |
| Confirmation email `email_sent` | Recommended |
| Failure to confirm | Operational only |

Consent wording version stored with grant (banner/DOI copy versioning pattern from tracking).

---

## Failure modes

| Case | Handling |
|---|---|
| Click after timeout | Offer re-subscribe form → new pending + DOI |
| Contact already subscribed | Confirm endpoint idempotent success |
| Erased contact | Token invalid; no resurrect |
| Spam folder | Resend API; marketer education |

---

## MVP checklist

- [ ] `pending` / `unconfirmed` statuses enforced at pre-send  
- [ ] Token confirm endpoint + consent_granted event  
- [ ] DOI Journey template  
- [ ] List `double_opt_in_required`  
- [ ] Handoff to Welcome on confirm  
- [ ] Resend with limits  

---

## References

- [welcome-series.md](./welcome-series.md)  
- [email-compliance.md](../Messaging/email-compliance.md)  
- [event-integrity.md](../Platform/event-integrity.md)  
- [contact-import-and-lists.md](../MarketingData/contact-import-and-lists.md)  
- [subscription-management.md](../Identity/subscription-management.md)  
