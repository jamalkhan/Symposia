# Data Retention and Billing

## Overview

Tenants pay for the storage they use and the data they retrieve. Pricing is transparent, usage is metered continuously, and the consequences of non-payment follow a defined, predictable schedule that prioritizes giving tenants time to recover before data is ever permanently deleted. Tenants also control their own data lifecycle: how long blobs are kept, when they expire, and what happens when they do.

---

## Storage Pricing Model

Storage is billed on a **per-byte-per-epoch** basis. There is no minimum storage commitment and no minimum blob size floor for billing purposes.

### What Is Billed

| Charge | Unit | Notes |
|---|---|---|
| **Storage** | Per GB stored, per epoch | Billed on the peak bytes stored during the epoch, not an average. A blob uploaded mid-epoch is billed for the full epoch. |
| **Egress** | Per GB retrieved | Charged when data is downloaded by a tenant's client. Intra-network replication traffic (node-to-node) does not count as tenant egress. |
| **Write requests** | Per 1,000 PUT/POST operations | Small per-operation fee to disincentivize extremely high-frequency tiny writes. |
| **Read requests** | Per 10,000 GET operations | Small per-operation fee. |
| **Replication overhead** | Included | The cost of maintaining the minimum copy count is absorbed by the network; tenants do not pay per-replica. |

### Pricing Tiers (Performance)

Storage pricing varies by the performance tier of the nodes where the data is placed (see [Performance Tiers and Workload Routing](../BlobStorage/performance-tiers-and-workload-routing.md)). Higher-tier nodes cost more per byte because they deliver better IOPS and lower latency.

| Tier | Storage Price (illustrative) | Egress Price (illustrative) |
|---|---|---|
| Tier 1 — Database Grade | 3× base rate | 2× base rate |
| Tier 2 — Hot Storage | 1.5× base rate | 1.5× base rate |
| Tier 3 — Warm Storage | 1× base rate | 1× base rate |
| Tier 4 — Cold / Archival | 0.5× base rate | 0.75× base rate |

The base rate is a governance parameter denominated in the native token. Tenants may query the current rate at any time via the API before committing storage.

### Region Pricing

Regions may carry a price modifier reflecting the relative cost and demand of nodes in that region. Overflow copies (placed outside the tenant's designated region) are priced at the rate of the region they land in. The total storage cost for a blob is the sum of the per-copy storage costs across all replicas.

### Payment Denomination

- The primary payment denomination is the **native token**.
- Supported stablecoins may be accepted at market rate as an alternative, swapped automatically to the native token at settlement. See [Payment and Stablecoin Integration](./payment-and-stablecoin-integration.md) for supported stablecoins, oracle mechanics, and swap failure handling.
- Prices are published in the native token. Tenants using stablecoins see a fiat-equivalent rate at time of payment.

---

## Prepaid Credit Model

- Storage is **prepaid**. Tenants purchase credits in advance; those credits are drawn down as storage and egress is consumed each epoch.
- Credits do not expire while the account is active.
- Tenants may top up their balance at any time.
- The current credit balance, estimated time remaining at current usage, and per-epoch burn rate are available via the API and tenant dashboard at all times.
- **No surprise bills**: tenants are never charged more than their available credit balance. If the balance is insufficient to cover an epoch's usage, the deficit triggers the low-balance process below — the tenant is never invoiced retroactively.

### Credit Alerts

The system sends alerts (via tenant-configured webhook or email) at the following thresholds:

- Balance drops below **30 days** of estimated remaining usage.
- Balance drops below **7 days** of estimated remaining usage.
- Balance drops below **48 hours** of estimated remaining usage.
- Balance reaches **zero**.

Thresholds are configurable by the tenant. HIPAA tenants are strongly encouraged to configure alerts and auto-top-up to avoid any service interruption to ePHI storage.

---

## Non-Payment and Data Retention Schedule

The following schedule applies when a tenant's credit balance reaches zero. It is designed to give tenants ample time to recover while protecting the network from indefinitely storing unpaid-for data.

### Day 0 — Balance Reaches Zero

- Immediate alert sent via all configured channels.
- **Writes are suspended**: no new data may be uploaded.
- **Reads continue**: existing data remains fully accessible. Tenants can still retrieve their data.
- Replication and redundancy maintenance continues. Data integrity is not affected.

### Day 0–7 — Grace Period

- Reads continue uninterrupted.
- Writes remain suspended.
- Daily reminder alerts are sent.
- If the tenant tops up their balance within this window, writes are immediately re-enabled and no data lifecycle events occur.

### Day 7–30 — Soft Suspension

- Reads begin to be rate-limited (tenants can still retrieve data, but at reduced throughput).
- Writes remain suspended.
- Weekly alerts are sent.
- The tenant's data remains fully intact and redundant on the network.
- Topping up the balance at any point in this window fully restores the account immediately.

### Day 30 — Soft Delete

- Blobs are **soft-deleted**: marked as deleted and no longer accessible via the API.
- Data is not yet removed from storage nodes.
- The tenant has a **60-day recovery window** from this point to restore their account by topping up their balance. Restoring the account un-soft-deletes all blobs.
- The 60-day window is extended to **12 months** for tenants who have ePHI-designated storage, to avoid accidental HIPAA compliance violations due to missed payments.

### Day 90 (Day 30 + 60) — Hard Delete

- If the balance has not been restored within the 60-day recovery window, blobs are **permanently and irrecoverably deleted** from all storage nodes.
- Because data is encrypted with tenant-managed keys, deletion of ciphertext is effectively complete — there is no plaintext to recover.
- A final deletion notice is sent to the tenant's configured contact before hard delete executes, with a 7-day final warning.
- Hard deleted data cannot be recovered under any circumstances, including by the platform operator.

### Exceptions

- **ePHI-designated blobs**: The soft-delete-to-hard-delete window is extended to 12 months as noted above. This also satisfies data recovery requirements in the event of a payment processing failure.
- **Legal hold**: A tenant may place a legal hold on their account, which suspends all deletion timelines regardless of balance. Legal holds require explicit tenant action and cannot be placed or removed by the platform unilaterally.

---

## Tenant-Controlled Retention Policies

Independent of billing, tenants may define retention policies on their own data. These policies govern how long blobs are kept and what happens when the retention period ends.

### Retention Rules

Retention rules may be applied at the bucket, folder prefix, or individual blob level:

- **Minimum retention period**: A blob cannot be deleted (by the tenant or by billing expiry) before this period elapses. Used for compliance purposes (e.g., keeping records for 7 years).
- **Maximum retention period (TTL / expiry)**: A blob is automatically deleted when this period elapses after the last write. Used for ephemeral data (e.g., temp files, session artifacts).
- **Version retention**: For versioned buckets, retain the last N versions and automatically purge older ones.

### Immutability

- A blob or bucket may be marked **immutable** for a defined duration. During that period, the blob cannot be overwritten, deleted, or have its metadata changed, even by the tenant owner.
- Immutability is enforced server-side and cannot be lifted early — not by the tenant, not by the platform, not by any administrative process. It expires at the stated time and not before.
- This satisfies WORM (Write Once, Read Many) requirements for regulated industries.

### Interaction with Billing Expiry

- A blob with an active minimum retention period or immutability lock **cannot be hard-deleted** by the billing expiry process. If a billing expiry event would hard-delete an immutable blob before its lock expires, the deletion is held until the lock expires.
- If the tenant's account is in this state, the platform flags it as a compliance hold and does not delete. The tenant is notified.

---

## Billing Records and Invoices

- Tenants may download a full itemized usage report for any epoch or date range via the API.
- Reports include: bytes stored per bucket, egress per bucket, request counts, tier breakdown, region breakdown, total cost in tokens, and any credits applied.
- Billing records are retained for a minimum of **7 years** for accounting and compliance purposes, regardless of whether the tenant account is active.
- For HIPAA tenants, billing records associated with ePHI storage are retained for **6 years** per HIPAA documentation requirements, but the 7-year general accounting retention supersedes this.
