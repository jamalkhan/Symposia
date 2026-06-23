# Quota Management

## Overview

Quotas allow tenants to set hard upper bounds on their own storage consumption, preventing runaway usage from bugs, misconfigured upload loops, or unexpected growth from driving unexpected bills. Quotas also allow the platform to enforce any contractual storage limits on specific accounts. Quota enforcement is a gate — when exceeded, writes are rejected cleanly — not a surprise on the next billing statement.

---

## Quota Hierarchy

Quotas apply at two levels. Both may be set independently and are enforced simultaneously — whichever limit is hit first applies.

### Account-Level Quota

A limit on the total storage used across all buckets in the tenant account.

- Set by the platform operator (e.g., as part of a free tier limit or enterprise contract cap).
- Also settable by the tenant themselves as a self-imposed spending guard.
- The effective account quota is the lower of the platform-imposed limit and any tenant-set self-limit.

### Bucket-Level Quota

A limit on the storage used within a specific bucket.

- Set by the tenant only (no platform-imposed bucket quotas by default).
- Useful for enforcing limits per application, per environment (prod vs. staging), or per customer in a multi-tenant SaaS architecture built on top of this platform.
- Multiple buckets may have quotas; they are enforced independently of each other.

---

## What Counts Toward Quota

| Counts | Does Not Count |
|---|---|
| Current stored blob content (bytes) | Bytes in transit (uploads in progress) |
| Non-current versions in versioned buckets | Bytes consumed by platform-internal replication (multiple copies do not multiply the quota charge) |
| Soft-deleted blobs within the recovery window | Incomplete multipart upload parts |
| User-defined metadata and tags (negligible, but counted) | |

**Key rule**: A tenant storing a 1 GB blob that is replicated to 5 nodes consumes 1 GB of quota — not 5 GB. Quota is measured against logical object size, not physical replica storage. This mirrors how S3 and Azure handle quota and storage billing.

Incomplete multipart upload parts do not count toward quota until the upload is completed. This prevents uploads from blocking quota before they are finalized. However, they do count toward billing (see [Garbage Collection](./garbage-collection.md) for cleanup).

---

## Quota Enforcement

The gateway checks quota on every write operation before initiating the upload fan-out:

1. Fetch current account and bucket usage from the metadata index.
2. Compare current usage + incoming object size against the applicable quota(s).
3. If either quota would be exceeded, reject the request immediately with `507 Insufficient Storage` and a body explaining which quota was exceeded and by how much.

### Race Conditions

In high-concurrency scenarios, multiple uploads may simultaneously pass the quota check before any of them have committed their usage. The gateway uses an **optimistic quota reservation** model:

- On quota check, the gateway reserves the incoming object's size against the quota atomically in the metadata index.
- If the upload fails (quorum not reached, client disconnected), the reservation is released.
- If two concurrent uploads would both exceed quota, only one reservation succeeds; the other receives `507`.

This prevents quota from being exceeded by more than the size of a single in-flight upload at worst.

---

## Quota Alerts

Tenants receive alerts when quota utilization crosses configured thresholds (separate from credit balance alerts):

| Default Threshold | Severity |
|---|---|
| 75% of account or bucket quota | `INFO` |
| 90% of account or bucket quota | `WARNING` |
| 100% (quota exceeded, writes blocked) | `CRITICAL` |

Thresholds are configurable. Alerts are delivered via the same webhook/email channels as other platform alerts (see [Tenant Observability](./tenant-observability.md)).

---

## Quota API

```
GET    /account/quota                   Get account-level quota settings and current usage
PUT    /account/quota                   Set tenant self-imposed account quota
DELETE /account/quota/self-limit        Remove tenant self-imposed limit (platform limit remains)

GET    /buckets/{bucket}/quota          Get bucket quota and current usage
PUT    /buckets/{bucket}/quota          Set bucket quota
DELETE /buckets/{bucket}/quota          Remove bucket quota (account limit still applies)
```

### Quota Response Shape

```json
{
  "limit_bytes": 107374182400,
  "used_bytes": 85899345920,
  "available_bytes": 21474836480,
  "utilization_pct": 80.0,
  "platform_imposed_limit_bytes": 107374182400,
  "tenant_self_limit_bytes": null,
  "effective_limit_bytes": 107374182400
}
```

---

## Object Count Quota

In addition to byte-based quotas, an optional **object count quota** may be set per bucket (see [Bucket Configuration](./bucket-configuration.md)). This prevents buckets from accumulating millions of tiny objects that create metadata index pressure, even if total bytes remain low.

Object count quotas are enforced the same way as byte quotas: checked at write time, with `507` returned on exceedance.

---

## Free Tier Enforcement

The platform's free tier (see [Network Bootstrapping and Cold Start](../Network/network-bootstrapping-and-cold-start.md)) is implemented via a platform-imposed account quota. When a new account is created:

- An account-level quota is set equal to the free tier storage limit (e.g., 10 GB).
- An egress quota is set equal to the free tier egress limit (e.g., 100 GB/month).
- When the free tier quota is reached, writes are blocked and the tenant is prompted to add payment credentials and purchase credits.

Free tier tenants who add payment credentials have their platform-imposed quota removed or increased to their contracted limit.
