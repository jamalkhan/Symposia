# Server-Side Copy

## Overview

Server-side copy allows a client to duplicate or move a blob within the platform without downloading and re-uploading the content. On a traditional centralized storage system this is trivial — the server copies bytes from one location to another. On a decentralized network where data lives on storage nodes, the implementation requires careful coordination. This file defines the semantics, mechanics, and constraints of server-side copy.

---

## Use Cases

- **Renaming an object**: Copy to a new key, then delete the original.
- **Promoting a draft**: Copy from `drafts/report.pdf` to `published/report.pdf`.
- **Cross-bucket duplication**: Copy an object from one bucket to another within the same tenant account.
- **Version snapshot**: Copy the current version of a mutable object to a versioned archive bucket.
- **Changing tier or region**: Copy to a new key with different tier or region metadata, then delete the original to "migrate" the object.

---

## Copy Semantics

A server-side copy produces a **new, independent object** at the destination key. After the copy:

- The source object is unchanged. It continues to exist with its original ETag, replicas, and metadata.
- The destination object has its own independent replica set, metadata record, and lifecycle.
- Modifying or deleting the destination has no effect on the source, and vice versa.
- The destination object's ETag is identical to the source's ETag (same ciphertext content hash), because the encrypted bytes are identical.

### Metadata Handling

By default, the destination object inherits the source object's user-defined metadata and content type. The following can be overridden in the copy request:

- User-defined metadata (full replacement, not merge).
- Content type.
- Tags (full replacement; source tags are not copied unless explicitly requested).
- Cache-Control, Content-Disposition, Content-Encoding (HTTP metadata).

System-managed metadata (size, ETag, replica list, created_at) is always set fresh for the destination — the destination's `created_at` is the time of the copy, not the source's creation time.

### Region and Tier

By default, the destination object is placed according to the destination bucket's region and tier defaults. The copy request may explicitly override region assignment and tier, allowing a copy to serve as a cross-region or cross-tier migration.

---

## Network-Level Mechanics

Because data is encrypted with client-managed keys and stored on decentralized nodes, server-side copy does not mean "copy bytes on the same disk." The implementation has two modes:

### Mode 1: Metadata-Only Copy (Same Ciphertext, New Metadata Record)

If the source and destination objects would use the same encryption (same tenant, same KMS key derivation, same content) and are placed in overlapping regions, the gateway can create a **new metadata record** pointing to the same underlying encrypted blob files on the same nodes.

- No data transfer between nodes occurs.
- The destination's replica list may initially overlap with the source's replica list.
- The destination is independently tracked: if the source is deleted, its replicas are only removed when they are no longer referenced by any other metadata record (reference counting).
- This mode is used when: same bucket (same default region and tier), no region/tier override in the copy request, and the gateway determines the node set is compatible.

**This mode is the default and preferred path for same-bucket copies.**

### Mode 2: Full Re-Replication Copy

If the destination requires a different set of nodes (different region, different tier, cross-bucket with different defaults), the gateway performs a full re-replication:

1. The gateway reads the encrypted blob from the best available source node.
2. It fans out the encrypted bytes to the target nodes for the destination, following the standard write path (placement rules, quorum requirements, fault domain rules).
3. A new metadata record is created for the destination pointing to the new replica set.
4. The source object's replica set is unchanged.

This mode requires data transfer proportional to the object size and counts against the tenant's egress for the read portion (reading from the source node).

### Mode Selection

The gateway selects Mode 1 when all of the following are true:
- Source and destination are in the same tenant account.
- The effective region assignment of the destination is compatible with (a subset of or equal to) the source's existing replica regions.
- The destination tier is ≤ the source tier (i.e., the source is on nodes at least as good as needed for the destination).

Otherwise, Mode 2 is used. The mode used is indicated in the copy response headers (`x-blob-copy-mode: metadata | replicated`).

---

## Conditional Copy

Server-side copy supports conditional headers to prevent overwriting an existing destination or to ensure the source hasn't changed since it was last read:

| Header | Applied To | Behavior |
|---|---|---|
| `x-copy-source-if-match` | Source | Proceed only if the source ETag matches. |
| `x-copy-source-if-none-match` | Source | Proceed only if the source ETag does not match. |
| `x-copy-source-if-modified-since` | Source | Proceed only if the source was modified after this date. |
| `x-copy-source-if-unmodified-since` | Source | Proceed only if the source was not modified after this date. |
| `If-None-Match: *` | Destination | Proceed only if the destination key does not already exist. |
| `If-Match` | Destination | Proceed only if the destination's current ETag matches. |

These follow the same semantics as conditional writes (see [Conditional Requests and Concurrent Writes](./conditional-requests-and-concurrent-writes.md)).

---

## API

### S3-Compatible

```
PUT /destination-bucket/destination-key
x-amz-copy-source: /source-bucket/source-key
x-amz-metadata-directive: COPY | REPLACE
x-amz-tagging-directive: COPY | REPLACE
x-amz-tagging: key1=val1&key2=val2  (if REPLACE)
```

Response: `200 OK` with XML body containing the ETag and last-modified of the new object.

### Azure-Compatible

```
PUT /destination-container/destination-blob
x-ms-copy-source: https://gateway.example/source-container/source-blob
```

### Native API

```
POST /copy

{
  "source": { "bucket": "src-bucket", "key": "src/file.dat" },
  "destination": { "bucket": "dst-bucket", "key": "dst/file.dat" },
  "metadata": "copy",          // "copy" or "replace"
  "metadata_overrides": {},    // only when metadata = "replace"
  "tags": "replace",           // "copy" or "replace"
  "tag_overrides": {},         // only when tags = "replace"
  "region_assignments": [],    // override destination region (optional)
  "tier": null                 // override destination tier (optional)
}
```

---

## Constraints

- **Cross-tenant copy is not permitted.** A credential cannot copy from another tenant's bucket, even if that bucket is public. Public buckets support read access for downloads, not server-side copies.
- **Maximum source object size**: 5 TB (same as the maximum object size). For objects larger than 5 GB, the copy is automatically chunked internally; the client does not need to use a multipart copy API.
- **Rate limiting**: Server-side copies in Mode 2 consume gateway read and write bandwidth. They are subject to the same per-tenant bandwidth quota as client-initiated transfers.
- **Billing**: Mode 1 copies are billed as a metadata operation (no storage or egress charge, small per-operation fee). Mode 2 copies are billed for the egress of reading from the source node plus the storage cost of the new replica set.
