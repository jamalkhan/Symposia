# Abuse Detection & Sender Reputation (MVP Minimum)

## Overview

Symposia’s email path (shared IP pool + marketer Email IP nodes + Critical/High priority overrides) is abusable: spam blasts, phishing via “OTP” templates, list bombing, compromised API keys, and cross-tenant reputation damage on the shared pool.

This document defines the **MVP minimum** controls required before **public marketer self-serve**. Full ML scoring and human SOC can deepen later; **automated gates must ship with MVP**.

Related: [Outbound Email Delivery](./outbound-email-delivery.md), [Campaigns](./campaigns.md) (priority allowlist, intent screening), [Email Compliance](./email-compliance.md), [Terms of Service / AUP](../Legal/terms-of-service-and-acceptable-use.md), [MVP.md](../MVP.md).

---

## Goals

| Goal | MVP bar |
|---|---|
| Protect **shared pool** | One bad actor cannot burn pool IPs for everyone |
| Protect **recipients** | Hard stops on critical bounce/complaint thresholds |
| Limit **blast radius** of compromised tenants | Progressive send limits → pause → freeze |
| Support **honest marketers** | Clear reasons, appeals, staged recovery |
| Support **Critical/High** types | Extra scrutiny; red-button override audited |

**Non-goal (MVP):** perfect spam classification of every creative; human-in-the-loop for every campaign.

---

## Trust tiers (tenant sending trust)

Every tenant has a **sending trust tier** that gates volume and features.

| Tier | How entered | Daily marketing cap (messages) | Shared pool? | Critical/High |
|---|---|---|---|---|
| **T0 — Probation** | New tenant | **500** (or warm-up IP schedule if dedicated) | Yes if eligible | Allowed with **intent score** + override audit |
| **T1 — Standard** | 14 days + healthy metrics | **10,000** | If still under shared-pool definition | Standard screening |
| **T2 — Established** | 60 days + healthy + domain aged | **100,000** | Usually dedicated Email IP | Standard |
| **T3 — Restricted** | Auto after violations | **0** marketing (transactional Critical may remain with review) | Removed from shared pool | High/Critical may be frozen |
| **T4 — Suspended** | Severe abuse | **All sends blocked** | N/A | Blocked |

Caps are **ceilings**; Email IP warm-up and frequency caps may be lower. Platform may raise T1/T2 caps via support.

### Promotion / demotion signals

**Promote T0→T1** when all hold for 14 days:

- Hard bounce rate **&lt; 2%** (trailing 7d, min 100 sends)  
- Complaint rate **&lt; 0.08%**  
- No open abuse incident  

**Demote or restrict** on thresholds in [Automated responses](#automated-responses).

---

## Reputation metrics (per tenant, per mail IP, per domain)

Computed continuously (at least hourly rollups + real-time counters for spikes).

| Metric | Window | Scope |
|---|---|---|
| Hard bounce rate | 24h, 7d | tenant, sending_domain, mail_ip |
| Soft bounce rate | 24h, 7d | same |
| Complaint (FBL) rate | 24h, 7d | same |
| Unsubscribe rate | 7d | tenant, domain |
| Spam-trap hits | lifetime + 7d | tenant (if trap network available) |
| Volume spike | vs trailing 7d median | tenant |
| Critical/High override rate | 7d | tenant (overrides with high intent score) |
| Intent screen score distribution | 7d | tenant |

---

## Signup & onboarding gates

| Control | MVP |
|---|---|
| Email/domain verification | Sending domain DNS verified before production marketing |
| Payment / credits | Non-zero credits or verified billing method before leaving T0 (policy-tunable) |
| Shared pool eligibility | Existing volume/list health rules in outbound-email-delivery |
| Disposable signup email | Block known disposable domains for tenant owner email |
| Velocity | Max **3** new sending domains / tenant / day; max **5** sender profiles / day (soft; support can raise) |

---

## Intent screening (High / Critical)

Already partially specified in Campaigns. MVP enforcement:

1. On activate of High/Critical campaign: run **rules + score** (promo language, link density, mismatched type vs body).  
2. Score bands:
   - **low** — proceed  
   - **medium** — warn; require confirm  
   - **high** — require **override attestation** (“big red button”) + audit log  
3. **Override budget:** max **20** high-score overrides / tenant / 7 days; excess → freeze High/Critical until review  
4. OTP/login templates: body length and pattern checks (numeric codes, short TTL language); promo CTAs → high score  

---

## Shared pool protection

| Control | MVP |
|---|---|
| Content score threshold | Stricter than dedicated; hold for review if high |
| Bounce **&gt; 2%** (24h, min 50 sends) | **Immediate pause** of tenant shared-pool sending |
| Complaint **&gt; 0.08%** | Pause + support review |
| Daily cap | **2,000**/day (existing) and trust-tier cap |
| Burst | Max 2× average hourly volume without warm-up exception |
| Ejection | Repeat pause within 30 days → force dedicated Email IP or T3 restricted |

One tenant’s pause **must not** stop other shared-pool tenants.

---

## Dedicated Email IP path

| Control | MVP |
|---|---|
| Warm-up schedule | Enforced (existing table) |
| Bounce/complaint thresholds | Same numbers as alerts; pause **that mail IP / domain** not necessarily whole platform if other IPs healthy |
| Open relay / unauthorized SMTP | Node health check; deregister endpoint |
| Cross-tenant use of node | Forbidden; detect and suspend |

---

## Automated responses

Progressive, deterministic, logged.

### Level 0 — Observe

Metrics only; dashboard green.

### Level 1 — Throttle

| Trigger (examples) | Action |
|---|---|
| Volume &gt; 5× 7d median hour | Cap hourly send rate to 2× median for 24h |
| Soft bounce elevated | Reduce concurrency to that domain’s MX |

### Level 2 — Pause marketing

| Trigger | Action |
|---|---|
| Hard bounce ≥ **5%** (24h, min 100) | Pause all **marketing** sends; transactional High/Critical may continue |
| Complaint ≥ **0.1%** (24h, min 500) | Pause marketing; alert |
| Shared pool bounce ≥ **2%** | Pause shared-pool path |
| Spam-trap hit (confirmed) | Pause marketing immediately |

Marketer sees status `sending_paused` with reason codes. API returns `403` / `451` with `error_code`.

### Level 3 — Freeze tenant sending

| Trigger | Action |
|---|---|
| Second Level 2 in **30 days** | All marketing + non-Critical transactional paused |
| Phishing / brand impersonation high confidence | Full send freeze |
| Manual admin / legal | Freeze |

### Level 4 — Suspend account

| Trigger | Action |
|---|---|
| Confirmed malware/phishing campaign | Suspend tenant; revoke API keys; shared-pool ban |
| Sanctions / ToS severe | Same + legal hold |

**Resume:** marketer fixes lists, requests review; platform requires bounce &lt; 2% on a **supervised** small send (e.g. 100 seed-like) before full restore.

---

## List bombing & import abuse

| Signal | Action |
|---|---|
| Import &gt; 50k rows with **no** consent columns + high unknown rate | Force attestation + T0 cap remains |
| Immediate send to entire import within 1h of first import | Throttle; require confirmation |
| Complaint spike within 24h of import | Pause; flag list quality |

Import erasure-hash and validation remain first line ([contact-import-and-lists](../MarketingData/contact-import-and-lists.md)).

---

## Compromised credentials

| Control | MVP |
|---|---|
| API key sudden geo/IP change + volume spike | Temporary pause + notify owner email |
| Concurrent sessions anomaly | Optional step-up (post-MVP MFA) |
| Rotate keys | Self-serve revoke; freeze until new key used from allowlisted IP (optional) |

---

## Cross-tenant signals

| Signal | Action |
|---|---|
| Same creative hash across many new tenants | Flag for review |
| Shared pool IP reputation drop (external RBL) | Divert new shared-pool signups; investigate top volume tenants |
| Known bad domain blocklist | Block sending domain verify |

Platform maintains internal **blocklists** (domains, URL hosts, email patterns). Not public.

---

## Human review (MVP-light)

| Queue | SLA |
|---|---|
| Shared-pool pause appeals | 2 business days |
| High-score Critical override spikes | 1 business day |
| Suspended accounts | Case-by-case |

MVP may be foundation ops, not full 24/7 SOC.

---

## Marketer visibility

Dashboard + API:

```
GET /marketing/reputation/summary
{
  "trust_tier": "T1",
  "status": "ok" | "throttled" | "paused" | "frozen",
  "bounce_rate_7d": 0.012,
  "complaint_rate_7d": 0.00005,
  "daily_cap_remaining": 8200,
  "reasons": [],
  "mail_ips": [ { "ip": "...", "warmup_day": 12, "status": "ok" } ]
}
```

Webhooks: `account.sending_paused`, `account.sending_resumed`, `account.trust_tier_changed`.

---

## Data model (sketch)

```sql
CREATE TABLE marketing.tenant_sending_trust (
  tenant_id           UUID PRIMARY KEY,
  trust_tier          TEXT NOT NULL DEFAULT 'T0',
  status              TEXT NOT NULL DEFAULT 'ok',
  daily_cap_override  INT,
  paused_reason       TEXT,
  paused_at           TIMESTAMPTZ,
  metrics_as_of       TIMESTAMPTZ,
  updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE marketing.abuse_events (
  event_id     UUID PRIMARY KEY,
  tenant_id    UUID NOT NULL,
  severity     TEXT NOT NULL,  -- observe | throttle | pause | freeze | suspend
  code         TEXT NOT NULL,  -- bounce_rate_high, complaint_rate_high, ...
  details      JSONB NOT NULL,
  created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

---

## Integration points

| System | Hook |
|---|---|
| Delivery pipeline | Check trust status + tier cap **before** queue; enforce pause |
| Campaign activate | Intent screen; override budget |
| Import jobs | List-bombing heuristics |
| Email IP agent | Health + unauthorized use |
| Billing | Optional: freeze sends also freezes net-new credit purchase for suspended |

---

## Escalation to full suite (post-MVP)

- ML content classifiers  
- Global seed/spam-trap federation  
- Automated phishing URL detonation  
- Cross-tenant graph of related accounts  
- 24/7 review ops  

MVP must not wait for these.

---

## MVP launch checklist

- [ ] Trust tier T0 defaults + promotion rules  
- [ ] Bounce/complaint auto-pause with marketer-visible reason  
- [ ] Shared-pool isolation of pause  
- [ ] Daily/hourly caps by tier  
- [ ] Critical/High override budget + audit  
- [ ] Reputation summary API  
- [ ] Admin freeze/suspend  
- [ ] Documented appeal path  

---

## References

- [outbound-email-delivery.md](./outbound-email-delivery.md)  
- [campaigns.md](./campaigns.md)  
- [email-compliance.md](./email-compliance.md)  
- [contact-import-and-lists.md](../MarketingData/contact-import-and-lists.md)  
- [terms-of-service-and-acceptable-use.md](../Legal/terms-of-service-and-acceptable-use.md)  
- [MVP.md](../MVP.md)  
