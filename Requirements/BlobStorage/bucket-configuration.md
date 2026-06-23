# Bucket Configuration

## Overview

A bucket is the top-level namespace for organizing blobs. Every blob belongs to exactly one bucket. Bucket configuration controls the default behavior for all blobs within it, the access and security model, and the lifecycle policies that govern data retention. This file enumerates every configurable property of a bucket.

---

## Bucket Identity

| Property | Description | Mutable |
|---|---|---|
| `name` | Globally unique within the tenant's namespace. 3–63 characters, lowercase alphanumeric and hyphens, must start and end with alphanumeric. | No |
| `created_at` | UTC timestamp set at creation. | No |
| `region_assignments` | Zero, one, or more regions where blobs in this bucket are placed by default. Inheritable at folder and object level. | Yes |
| `default_tier` | Default performance tier (1–4) for new blobs. Overridable per object. | Yes |

---

## Access and Security

| Property | Description | Default |
|---|---|---|
| `public_access` | Whether unauthenticated reads are permitted. Options: `none` (fully private), `read` (anyone can GET/HEAD any blob). | `none` |
| `public_access_block` | When `true`, prevents any public access setting or presigned URL from being configured, regardless of other settings. Useful for accounts with sensitive data that should never be publicly accessible. | `false` |
| `acl` | Access control list entries granting specific credentials access to this bucket beyond their normal scope. Supports read-only and read-write entries. | Empty |
| `cors_rules` | CORS configuration for this bucket. See [CORS](./cors.md). | Empty (CORS disabled) |
| `presigned_url_max_ttl` | Maximum permitted lifetime for presigned URLs generated for this bucket. Shorter than the platform maximum (7 days) to restrict exposure of sensitive buckets. | 7 days |
| `hipaa_designated` | When `true`, marks this bucket as containing ePHI. Enforces additional controls: public access permanently blocked, extended audit log retention, BAA requirement for node placement. | `false` |
| `legal_hold` | When `true`, prevents all deletion operations and billing-expiry hard deletes on all blobs in the bucket. Must be explicitly released. | `false` |

---

## Versioning

| Property | Description | Default |
|---|---|---|
| `versioning` | Controls object versioning. Options: `disabled` (single version per key), `enabled` (all versions retained), `suspended` (no new versions created; existing versions retained). | `disabled` |
| `version_id_format` | Format for version identifiers. Default: monotonic integer. Alternative: timestamp-based UUID. | `integer` |

Once versioning is enabled on a bucket, it cannot be fully disabled — only suspended. This prevents accidental data loss after version history has accumulated.

---

## Lifecycle Rules

Lifecycle rules define automated actions taken on objects based on age, version count, or status. Multiple rules may be defined; they are evaluated independently.

| Rule Property | Description |
|---|---|
| `id` | Unique identifier for the rule within the bucket. |
| `filter` | Optional prefix and/or tag filter narrowing which objects the rule applies to. |
| `expiration_days` | Delete objects (current version) after this many days since creation. |
| `noncurrent_version_expiration_days` | Delete non-current versions after this many days since they became non-current. |
| `noncurrent_version_keep_count` | Keep only the N most recent non-current versions; delete the rest. |
| `abort_incomplete_multipart_days` | Abort and clean up incomplete multipart uploads after this many days of inactivity. Overrides the bucket default of 7 days. |
| `tier_transition_days` | Transition objects to a lower performance tier after this many days since creation. Specify the target tier. |

Lifecycle rules interact with retention and legal holds: an object with a minimum retention period or a legal hold is not affected by a lifecycle expiration rule until the hold expires.

---

## Event Notifications

| Property | Description | Default |
|---|---|---|
| `notification_subscriptions` | List of event subscription IDs attached to this bucket. Each subscription defines which events and which destination. See [Blob Event Notifications](./blob-event-notifications.md). | Empty |

---

## Quota

| Property | Description | Default |
|---|---|---|
| `max_size_bytes` | Maximum total bytes stored in this bucket. Writes are rejected with `507` when exceeded. | Unlimited (account-level quota applies) |
| `max_object_count` | Maximum number of objects in this bucket. | Unlimited |

See [Quota Management](./quota-management.md) for account-level quota controls.

---

## Logging

| Property | Description | Default |
|---|---|---|
| `access_log_destination` | Bucket and optional prefix where access logs for this bucket are written. If set, every request to this bucket generates a log entry written as an object in the destination bucket. | Disabled |
| `access_log_format` | Format for log objects: `json` or `csv`. | `json` |

Logging to a destination bucket counts against that bucket's quota and storage billing.

---

## Replication and Redundancy Overrides

| Property | Description | Default |
|---|---|---|
| `min_replica_count` | Minimum number of copies required, overriding the network default for this bucket. May not be set lower than the network minimum. | Network default |
| `overflow_regions_allowed` | Whether blobs in this bucket may have overflow copies placed in regions outside the bucket's `region_assignments`. Setting to `false` on a single-region bucket means all copies must stay in that region — fewer total copies may be achievable if the region has limited nodes. | `true` |

---

## Immutability

| Property | Description | Default |
|---|---|---|
| `default_immutability_days` | If set, all new objects in this bucket are automatically locked as immutable for this many days after creation. Tenants cannot delete or overwrite them during this period. Used for WORM compliance. | Not set |
| `immutability_mode` | `governance` (admin credentials can override the lock) or `compliance` (no one can override — not even the platform). HIPAA and regulatory use cases should use `compliance`. | `governance` |

---

## Bucket Management API

```
POST   /buckets                         Create a bucket
GET    /buckets                         List all buckets for the tenant
GET    /buckets/{bucket}                Get bucket configuration
PUT    /buckets/{bucket}                Update bucket configuration (partial update supported)
DELETE /buckets/{bucket}                Delete bucket (must be empty)
GET    /buckets/{bucket}/stats          Get usage stats (object count, total bytes, by tier/region)
```

### Deletion Rules

A bucket may only be deleted when it contains zero objects (including soft-deleted objects and non-current versions). Attempting to delete a non-empty bucket returns `409 Conflict`. Tenants must delete all objects (or wait for lifecycle expiry) before the bucket itself can be removed.

---

## Inheritance Model

Bucket configuration properties cascade to objects in the following order of precedence (highest wins):

```
Object-level setting
  → Folder/prefix-level policy (if defined via lifecycle or tag rules)
    → Bucket-level default
      → Account-level default
        → Network default
```

An object-level `region_assignments` overrides the bucket default. An object without an explicit region assignment inherits the bucket's `region_assignments`.
