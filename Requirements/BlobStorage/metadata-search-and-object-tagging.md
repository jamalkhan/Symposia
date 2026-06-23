# Metadata Search and Object Tagging

## Overview

S3-compatible prefix listing is sufficient for navigating a known folder structure but is inadequate for finding objects based on their properties. Data engineering workloads, audit queries, lifecycle management, and the Postgres/Neon layer built on top of this storage system all benefit from richer querying: find all objects uploaded in a date range, find all objects larger than a threshold, find all objects with a specific tag, find all objects belonging to a specific workflow run.

Object tagging is the mechanism by which tenants attach structured, queryable labels to objects. Metadata search is the query layer that makes those labels and built-in object properties findable.

---

## Object Tagging

Tags are key-value pairs attached to an object, separate from user-defined metadata. The distinction:

| | Tags | User-Defined Metadata |
|---|---|---|
| Purpose | Classification, filtering, billing allocation, lifecycle targeting | Arbitrary application data |
| Queryable via search | Yes | No (metadata values are not indexed) |
| Lifecycle rule targeting | Yes (filter by tag) | No |
| Billing allocation | Yes (cost per tag set is reportable) | No |
| Size limits | 10 tags per object, key ≤128 chars, value ≤256 chars | 2KB total per object |
| Returned on HEAD | Yes | Yes |
| Stored separately from content | Yes | Yes |

### Tag Operations

```
GET    /buckets/{bucket}/{key}?tagging          Get object tags
PUT    /buckets/{bucket}/{key}?tagging          Replace all tags on an object
DELETE /buckets/{bucket}/{key}?tagging          Remove all tags from an object
```

Tag updates do not change the object's ETag or last-modified timestamp. They do generate an audit log event.

### Tag Naming Rules

- Key: printable ASCII characters, no spaces. Case-sensitive. Must not start with `sys:` (reserved for system tags).
- Value: printable ASCII characters, spaces allowed.
- Duplicate keys within a single tag set are not permitted.

### System Tags

The platform applies a set of read-only `sys:` prefixed tags to every object automatically:

| Tag | Value |
|---|---|
| `sys:tier` | Performance tier at time of upload (e.g., `2`) |
| `sys:region` | Primary region of the object's first replica |
| `sys:replica-count` | Current healthy replica count |
| `sys:upload-source` | `s3`, `azure`, or `native` — which interface was used to upload |

System tags are visible in tag responses and queryable via search but cannot be set or deleted by tenants.

---

## Metadata Search

The metadata search API provides a structured query interface over object properties and tags within a tenant's namespace. It is separate from the S3/Azure list interface and is accessed via the platform-native API.

### Queryable Fields

| Field | Type | Description |
|---|---|---|
| `bucket` | string | Filter to a specific bucket (required unless querying account-wide). |
| `key_prefix` | string | Prefix filter on the object key. |
| `key_suffix` | string | Suffix filter on the object key (e.g., `.parquet`). |
| `size_bytes_min` | integer | Minimum object size in bytes. |
| `size_bytes_max` | integer | Maximum object size in bytes. |
| `created_after` | timestamp | Objects created after this UTC timestamp. |
| `created_before` | timestamp | Objects created before this UTC timestamp. |
| `modified_after` | timestamp | Objects last modified after this UTC timestamp. |
| `modified_before` | timestamp | Objects last modified before this UTC timestamp. |
| `etag` | string | Exact ETag match. |
| `content_type` | string | Exact or prefix match on content type (e.g., `image/`, `application/json`). |
| `tier` | integer | Performance tier (1–4). |
| `tag:{key}` | string | Objects where the tag `{key}` equals the given value. |
| `tag:{key}_exists` | boolean | Objects that have (or don't have) a tag with key `{key}`. |
| `has_version` | boolean | Objects with more than one version. |
| `replica_count_lt` | integer | Objects with fewer than N healthy replicas (useful for finding under-replicated objects). |

### Query Request Format

```json
POST /search

{
  "bucket": "my-bucket",
  "filters": [
    { "field": "created_after", "value": "2026-01-01T00:00:00Z" },
    { "field": "size_bytes_min", "value": 1048576 },
    { "field": "tag:pipeline-run", "value": "run-20260601" }
  ],
  "sort": { "field": "created_at", "order": "desc" },
  "limit": 100,
  "cursor": null
}
```

Multiple filters are AND-combined. OR logic within a single field is not supported in v1 (future: tag value arrays).

### Query Response Format

```json
{
  "objects": [
    {
      "bucket": "my-bucket",
      "key": "data/output-001.parquet",
      "size_bytes": 2097152,
      "etag": "\"a3f1b2c4...\"",
      "content_type": "application/octet-stream",
      "created_at": "2026-06-01T14:32:00Z",
      "modified_at": "2026-06-01T14:32:00Z",
      "tier": 2,
      "replica_count": 5,
      "tags": {
        "pipeline-run": "run-20260601",
        "stage": "output",
        "sys:tier": "2"
      }
    }
  ],
  "next_cursor": "eyJrZXkiOiJkYXRhL291dHB1dC0wMDIu...",
  "total_count": 847
}
```

Pagination uses an opaque cursor (not a page number). Pass `next_cursor` from one response as `cursor` in the next request. A null `next_cursor` means the result set is exhausted.

### Limits and Performance

- Maximum 1,000 results per page.
- Maximum query scope: one bucket per query (account-wide queries across all buckets are a future capability).
- Search results reflect the metadata index, which has an eventual consistency lag of up to 60 seconds after writes (same as LIST operations).
- Search queries are not suitable for real-time, sub-second use cases. For those, use the event notification system (see [Blob Event Notifications](./blob-event-notifications.md)).
- Search is rate-limited separately from object read/write operations: 60 queries per minute per credential.

---

## Search Index Architecture

The metadata search index is maintained as an off-chain index (separate from the blockchain) that is updated by the gateway after every successful write, delete, or tag update. The index is:

- **Eventually consistent**: Updated asynchronously after the metadata commit. Lag is typically under 5 seconds, with a maximum SLA of 60 seconds.
- **Tenant-scoped**: Each tenant's index is isolated. Cross-tenant search is architecturally impossible.
- **Authoritative source**: The blockchain/metadata store is the source of truth. The search index is a derived projection. If the index becomes inconsistent, it can be rebuilt from the metadata store without data loss.
- **Not on-chain**: The search index is not stored on the L3 chain. It is an off-chain service operated by the platform. This keeps chain transaction costs low and allows the index to be rebuilt or migrated independently.

---

## Lifecycle Rule Integration

Lifecycle rules (see [Bucket Configuration](./bucket-configuration.md)) can filter by tags:

```json
{
  "id": "expire-temp-files",
  "filter": {
    "prefix": "tmp/",
    "tags": { "temp": "true" }
  },
  "expiration_days": 7
}
```

Only objects matching both the prefix and all specified tags are affected by the rule. This allows fine-grained lifecycle management without touching objects that happen to share a prefix.
