# Blob Event Notifications

## Overview

Tenants need to react to things that happen to their data — a file is uploaded, a processing job should start; a blob is deleted, a cache should be invalidated; a blob's replica count drops, an alert should fire. Event notifications are the mechanism by which the platform pushes these events to tenant systems in real time, rather than requiring tenants to poll the API.

This is one of the most widely used features in S3-compatible storage systems. It is a prerequisite for the platform to serve as the backbone of event-driven architectures.

---

## Event Types

### Blob Lifecycle Events

| Event | Description |
|---|---|
| `blob.created` | A new blob was successfully written (all minimum replicas confirmed). |
| `blob.updated` | An existing blob was overwritten with a new version. |
| `blob.deleted` | A blob was deleted (soft or hard, configurable). |
| `blob.restored` | A soft-deleted blob was restored within the recovery window. |
| `blob.metadata_updated` | A blob's user-defined metadata was changed without replacing the content. |
| `blob.copy_completed` | A server-side copy operation completed. |
| `blob.multipart_completed` | A multipart upload was completed and the blob is now available. |
| `blob.multipart_aborted` | A multipart upload was explicitly aborted or expired. |

### Replication and Health Events

| Event | Description |
|---|---|
| `blob.replica_lost` | A replica of this blob went offline; repair has been triggered. |
| `blob.replica_repaired` | A lost replica has been successfully restored on a new node. |
| `blob.below_minimum_replicas` | The blob's healthy replica count dropped below the configured minimum. |
| `blob.repair_complete` | Repair is complete; blob is back to its target replica count. |
| `blob.tier_changed` | The blob's primary serving tier changed (promotion or demotion). |

### Billing Events

| Event | Description |
|---|---|
| `account.credit_low` | Credit balance crossed a low threshold. |
| `account.credit_critical` | Credit balance crossed the critical threshold. |
| `account.credit_zero` | Credit balance reached zero; writes suspended. |
| `account.soft_suspended` | Account entered soft suspension (reads rate-limited). |

---

## Subscription Model

Tenants create **event subscriptions** that define:

1. **Source scope**: Which buckets and/or key prefixes generate events for this subscription.
2. **Event filter**: Which event types to include. Multiple event types per subscription are allowed.
3. **Destination**: Where events are delivered.
4. **Optional key suffix filter**: Only deliver events for keys matching a suffix (e.g., `.jpg`, `.parquet`).

A tenant may have multiple subscriptions with overlapping scopes. The same event may be delivered to multiple destinations if multiple subscriptions match it. There is no deduplication across subscriptions.

### Subscription Scope Examples

```
# All events on all blobs in a bucket
bucket: "my-bucket"
events: [blob.created, blob.deleted]

# Only events on blobs under a specific prefix
bucket: "uploads"
prefix: "incoming/"
events: [blob.created]

# Only .parquet files anywhere in the account
prefix: ""  # account-wide
suffix: ".parquet"
events: [blob.created, blob.multipart_completed]
```

---

## Delivery Destinations

### Webhook (HTTP POST)

- The platform delivers a signed HTTP POST to a tenant-configured URL.
- Payloads are JSON (see Payload Format below).
- The signature is an HMAC-SHA256 of the raw payload body, using a secret provided by the tenant at subscription creation time. The signature is included in the `X-Blob-Signature` header, allowing the receiver to verify authenticity.
- The receiving endpoint must respond with HTTP 2xx within 30 seconds. Any other response (including timeouts) is treated as a delivery failure and triggers retry.
- **Retry policy**: Failed deliveries are retried with exponential backoff: 30s, 2m, 10m, 1h, 6h. After 6 hours of failed delivery, the event is considered undeliverable and is moved to the dead-letter store.
- **Dead-letter store**: Undeliverable events are retained for 7 days and queryable via the API. Tenants can inspect and replay them after fixing the receiving endpoint.

### Message Queue (future)

- Integration with external message queues (e.g., compatible with AMQP, AWS SQS-compatible interfaces) is a planned destination type for tenants who prefer pull-based consumption over push.

---

## Payload Format

All event payloads share a common envelope:

```json
{
  "id": "evt_01j9abc123",
  "type": "blob.created",
  "timestamp": "2026-06-21T14:32:00.000Z",
  "tenant_id": "tnt_xyz",
  "subscription_id": "sub_abc",
  "data": {
    "bucket": "my-bucket",
    "key": "uploads/photo.jpg",
    "size_bytes": 2048576,
    "content_type": "image/jpeg",
    "etag": "sha256:abc123...",
    "version_id": "v_001",
    "region_assignments": ["us-east"],
    "tier": 2,
    "replica_count": 5,
    "user_metadata": {
      "uploaded-by": "user-42"
    }
  }
}
```

- `id` is globally unique and stable. Replaying an event delivers the same `id`, allowing receivers to implement idempotent processing.
- `timestamp` is the time the event was generated by the platform, not the time of delivery.
- The `data` object shape varies by event type; each event type has a documented schema.

---

## Ordering and Delivery Guarantees

- Events are delivered **at least once**. Receivers must be idempotent (use the `id` field to detect duplicates).
- Events within the same bucket and key are delivered **in order of occurrence** on a best-effort basis. Under high load or retry conditions, ordering is not strictly guaranteed.
- Events are not transactional with the operation that generated them. A `blob.created` event may arrive slightly after the blob is queryable via the API, but the blob is always queryable before the event fires.

---

## Management API

```
POST   /subscriptions                  Create a new subscription
GET    /subscriptions                  List all subscriptions
GET    /subscriptions/{id}             Get a specific subscription
PUT    /subscriptions/{id}             Update a subscription
DELETE /subscriptions/{id}             Delete a subscription
POST   /subscriptions/{id}/test        Send a test event to the destination
GET    /subscriptions/{id}/dead-letter List undeliverable events
POST   /subscriptions/{id}/dead-letter/replay  Replay dead-letter events
```

The test endpoint sends a synthetic `blob.created` event with a clearly marked `"test": true` field in the payload, allowing tenants to verify their endpoint configuration without creating real blobs.
