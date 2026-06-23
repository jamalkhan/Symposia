# SLA and Availability Guarantees

## Overview

An SLA (Service Level Agreement) is a public commitment about what the platform guarantees to tenants and what they receive if those guarantees are not met. Without a defined SLA, enterprise tenants cannot include this platform in their own availability planning, and there is no objective basis for dispute resolution when things go wrong.

This file defines the platform's availability targets, how they are measured, and the remedies available when they are not met.

---

## Durability

**Target: 99.999999999% (eleven nines) annual durability per object.**

Durability is the probability that a stored object will not be permanently lost in a given year. Eleven nines means that storing 1 million objects for 1 year results in an expected loss of less than 0.001 objects — effectively zero in practice.

This target is achieved through:
- Minimum replication factors (4–7 copies depending on region assignment).
- Mandatory geographic distribution across fault domains.
- Continuous integrity verification and automatic repair.
- Penalty and slashing systems that economically punish node operators for data loss.

Durability is a statistical target, not a per-object guarantee. It is calculated based on the design parameters of the replication system and is comparable to the durability figures published by AWS S3 and Azure Blob Storage.

**Durability excludes:**
- Data loss caused by the tenant (accidental deletion, key loss).
- Data that the tenant configured for automatic expiry.
- Data on accounts suspended or terminated for non-payment after the retention schedule has run its course.

---

## Availability

Availability is the percentage of time that stored data can be successfully read by authorized clients. It is measured separately for read operations and write operations.

| Operation | Monthly Availability Target |
|---|---|
| **Read (GET)** | 99.9% |
| **Write (PUT)** | 99.5% |
| **List / Metadata (LIST, HEAD)** | 99.9% |

### Measurement

- Availability is calculated per calendar month per tenant account.
- An unavailability event is any period during which the API returns 5xx errors for more than 1% of requests in a 5-minute window, sustained for more than 5 minutes.
- Scheduled maintenance windows announced at least 72 hours in advance are excluded from availability calculations.
- Errors caused by the tenant (invalid credentials, malformed requests, exceeding rate limits) are excluded.
- Errors on blobs that the tenant has configured in regions with insufficient node coverage are excluded — the tenant accepted that risk by choosing an under-served region.

### Measurement Method

- Availability is measured using synthetic monitoring: the platform continuously makes read and write requests to canary objects in each region from external vantage points.
- Tenants may also self-report outages via a documented process; self-reported incidents are investigated and, if confirmed, applied to the SLA calculation.

---

## Latency Targets (Informational)

Latency targets are informational benchmarks, not SLA commitments, because latency is heavily dependent on the tenant's geographic location relative to their data and the performance tier chosen.

| Tier | Median TTFB Target | P99 TTFB Target |
|---|---|---|
| Tier 1 — Database Grade | ≤ 5 ms | ≤ 20 ms |
| Tier 2 — Hot Storage | ≤ 25 ms | ≤ 100 ms |
| Tier 3 — Warm Storage | ≤ 150 ms | ≤ 500 ms |
| Tier 4 — Cold / Archival | ≤ 1,000 ms | No target |

These targets apply for clients accessing data in the same region as the stored blob. Cross-region access will be slower by the nature of physical distance.

---

## Service Credits

When the platform fails to meet its availability targets in a given calendar month, affected tenants receive service credits applied to their account balance.

| Monthly Availability Achieved | Credit Applied |
|---|---|
| 99.0% – 99.5% (below Read target) | 10% of monthly storage cost |
| 95.0% – 99.0% | 25% of monthly storage cost |
| < 95.0% | 50% of monthly storage cost |

Credits apply to the affected operation type (read or write) and the affected region(s), not to the entire account.

### Credit Claim Process

- Credits for confirmed incidents (identified by synthetic monitoring) are applied automatically within 3 business days of the end of the affected month.
- Tenants may also submit a credit claim within 30 days of an incident they experienced, with timestamps and error logs. The platform investigates and applies credits within 10 business days.

### Credit Limitations

- Credits are the sole remedy for availability failures under the standard SLA. They do not constitute acknowledgment of liability for consequential damages.
- Credits cannot be exchanged for cash or tokens; they apply only to future platform usage.
- A tenant must have a positive account balance at the time of the incident to be eligible for credits.
- Enterprise contracts may negotiate different remedies, including cash compensation, through a separate agreement.

---

## Exclusions

The SLA does not apply to:

- The testnet environment (no availability commitment).
- Local development mode.
- Incidents caused by the tenant's own actions (credential misuse, code bugs, incorrect region configuration).
- Events outside the platform's reasonable control: natural disasters, upstream internet infrastructure failures (backbone outages), and similar force majeure events. These are defined in the Terms of Service.
- Blobs whose region assignment cannot be satisfied due to insufficient node coverage — the tenant receives a warning at write time and accepts the reduced redundancy.
- Accounts in the non-payment suspension or soft-delete phase.

---

## Status Page

- A public status page publishes real-time and historical availability per region.
- Incidents are posted within 15 minutes of detection, with regular updates until resolution.
- Post-incident reports (post-mortems) are published within 5 business days of resolution for any incident that caused more than 30 minutes of degraded availability.
- Post-mortems include: timeline, root cause, impact assessment, and specific remediation actions taken.
- The status page is hosted on independent infrastructure from the platform itself so it remains accessible during platform incidents.
- Tenants can subscribe to status page updates via email or webhook.

---

## Planned Maintenance

- Planned maintenance that may affect availability is announced at least 72 hours in advance via the status page and email notification.
- The platform targets zero-downtime deployments. Planned maintenance that requires downtime is scheduled during low-traffic windows (historically: nights and weekends in the primary tenant geography).
- Planned maintenance is excluded from the SLA availability calculation as described above.
