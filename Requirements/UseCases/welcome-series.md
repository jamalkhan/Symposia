# Use Case: Welcome Series / List Signup (E2E)

**Status: Specced (MVP P0)**  
**Template key:** `welcome_series`

## Goal

Onboard a **newly subscribed** contact: set expectations, reinforce brand value, and drive a first conversion (or engagement) with a short linear email series.

Works with [Double Opt-In](./double-opt-in.md): if DOI is required, Welcome starts **only after** confirmation.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Sender profile + email path | Shared pool or Email IP |
| Target list(s) | Standard list; optional DOI flag |
| Contact marketable | `email_status = subscribed` at **each** send |
| Template clone | `welcome_series` |

---

## Triggers (any may enroll — re-entry blocks duplicates)

| Trigger | When to use |
|---|---|
| **`added_to_list`** | Primary — contact joins Welcome-enabled list |
| **`contact.created`** with `email_status = subscribed` | Direct API signup without separate list event |
| **`form_submit`** + identify | Web signup that creates subscribed contact (DOI off) |
| **DOI complete** | Auto-enroll from DOI Journey action |
| **Import** | Option `enroll_welcome_list_ids` only for rows that land `subscribed` (not pending) |

**Do not enroll** if `email_status ∈ (pending, unconfirmed, unsubscribed, bounced, complained)`.

### Trigger filter example

```json
{
  "event_type": "list.member_added",
  "all": [
    { "field": "list_id", "op": "in", "value": ["{{ campaign.welcome_list_ids }}"] },
    { "field": "contact.email_status", "op": "eq", "value": "subscribed" }
  ]
}
```

---

## Campaign + Journey (default template)

| Field | Default |
|---|---|
| `campaign_type` | `triggered` |
| `execution_mode` | `journey_backed` |
| `category` | `marketing` |
| `priority` | `marketing` |
| `re_entry_policy` | **`no_re_entry`** (once per contact lifetime for this Campaign) |
| Emails | **3** (marketer can disable 2/3) |

```
[Trigger: subscribed + list add / DOI handoff]
        │
        ▼
[Action: Email 1 — Welcome + expectations]
        │  immediate (or wait until quiet hours / STO off for triggered)
        ▼
[Wait: 2 days]
        │  global exit: unsubscribed | deleted | complained
        ▼
[Action: Email 2 — Brand story / value / content]
        │
        ▼
[Wait: 3 days]
        │
        ▼
[Action: Email 3 — Primary CTA / offer]
        │
        ▼
[Exit: completed]
```

Optional branch after email 1: if `email_opened` within 2 days → path A (deeper content); else path B (simpler CTA). **MVP default: linear** (no branch) for simplicity.

---

## Context / personalization

```json
{
  "trigger_event": "list.member_added",
  "event_data": {
    "list_id": "uuid",
    "list_name": "Newsletter",
    "signup_source": "footer_form",
    "welcome_offer_code": "WELCOME10"
  }
}
```

Liquid: `{{ contact.first_name }}`, `{{ journey.event_data.list_name }}`, `{{ journey.event_data.welcome_offer_code }}`.

---

## Ordering with Double Opt-In

```
Signup (pending)
    → DOI Campaign (confirm email)
    → click confirm → subscribed + consent_granted
    → enroll Welcome Campaign
    → Emails 1–3
```

If DOI **off**:

```
Signup (subscribed)
    → enroll Welcome immediately
```

**Never** run Welcome and DOI marketing content in parallel for the same pending contact.

---

## List / Campaign binding

| Config | Purpose |
|---|---|
| `welcome_list_ids[]` | Which list adds enroll Welcome |
| `require_subscribed` | Always true for send actions |
| `skip_if_already_purchased` | Optional: exit if purchase in last N days |

---

## Eligibility at each send

Same as any marketing send: subscribed, not suppressed, frequency caps, quiet hours, trust tier not paused. Mid-series unsub → global exit, no further emails.

---

## Re-entry

| Policy | Rationale |
|---|---|
| `no_re_entry` default | Welcome is once |
| Exception | New email address = new contact row → new welcome OK |
| Same contact re-added to list years later | Still blocked by no_re_entry unless marketer changes policy to `re_entry_after_cooldown` (e.g. 365 days) |

---

## Events

| Event | Role |
|---|---|
| `list.member_added` / `contact.created` | Trigger |
| `journey_enrolled`, step, `email_*` | Execution |
| `journey_exited` | Complete or unsub |

---

## MVP checklist

- [ ] Template clone `welcome_series`  
- [ ] Trigger on list add + DOI handoff  
- [ ] `no_re_entry` enforced  
- [ ] Three-email linear Journey with waits  
- [ ] Pre-send blocks pending contacts  
- [ ] Works with import of already-subscribed contacts (optional enroll flag)  

---

## References

- [double-opt-in.md](./double-opt-in.md)  
- [contact-import-and-lists.md](../MarketingData/contact-import-and-lists.md)  
- [journeys.md](../Journeys/journeys.md)  
- [campaigns.md](../Messaging/campaigns.md)  
- [MVP.md](../MVP.md)  
