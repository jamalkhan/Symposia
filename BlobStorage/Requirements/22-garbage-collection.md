# Garbage Collection

## Overview

Storage systems accumulate orphaned data over time: incomplete multipart uploads that were never finished, soft-deleted blobs waiting to be purged, stale metadata for replicas on nodes that have since left the network, and expired versioned blobs. Without a systematic garbage collection (GC) process, these artifacts consume storage capacity, inflate billing, and create metadata inconsistencies. GC must be continuous, prioritized, and safe — it must never delete data that is still needed.

---

## Categories of Garbage

### 1. Incomplete Multipart Uploads

A multipart upload is initiated with a `CreateMultipartUpload` call. Parts are uploaded individually. The upload is finalized with `CompleteMultipartUpload`. If the final call is never made — due to a client crash, a bug, or abandonment — the uploaded parts remain on storage nodes indefinitely, consuming real disk space and counting toward used capacity.

**GC rules:**
- By default, incomplete multipart uploads are automatically aborted and their parts deleted after **7 days** of inactivity.
- Tenants may configure a shorter or longer TTL per bucket (minimum: 1 hour; maximum: 30 days).
- Inactivity is defined as: no `UploadPart` call received for the upload ID within the TTL window.
- An `blob.multipart_aborted` event is emitted when GC cleans up an incomplete upload (see Blob Event Notifications requirements).
- Tenants are billed for the storage consumed by uploaded parts during the period they exist, before GC removes them.

### 2. Soft-Deleted Blobs

Blobs in the soft-delete state (marked as deleted but not yet physically removed) occupy disk space on storage nodes. Physical removal must follow the retention schedule (see Data Retention and Billing requirements) but is a GC operation.

**GC rules:**
- After the soft-delete recovery window expires (30 days for standard accounts, up to 12 months for ePHI accounts), blobs are queued for physical deletion.
- Physical deletion propagates to all replicas within 72 hours of being queued.
- GC confirms deletion from each replica and updates the metadata record. The metadata record itself is retained for audit purposes for the duration defined in Audit Logging requirements.
- Blobs with an active immutability lock or legal hold are excluded from deletion GC regardless of soft-delete status — the lock takes precedence.

### 3. Expired Versioned Blobs

When a bucket has versioning enabled and a lifecycle policy defines a maximum version count or version age, old versions are eligible for GC.

**GC rules:**
- Lifecycle-expired versions are soft-deleted first, then follow the physical deletion schedule above.
- Delete markers with no remaining non-deleted versions underneath them are also cleaned up.
- GC respects any minimum retention period defined on the bucket — a version cannot be GC'd before its minimum retention period, even if the lifecycle policy would otherwise expire it.

### 4. Stale Replica Metadata

When a node leaves the network (decommissioned, slashed off, or simply disappeared), the blob metadata still references that node as a replica location. This stale reference is never valid again and must be cleaned.

**GC rules:**
- When a node is confirmed offline for longer than the grace period (15 minutes), its replica references are marked `Offline` in blob metadata.
- A background process scans for blobs with any `Offline` replicas and triggers re-replication (see Redundancy requirements).
- Once re-replication is confirmed and the new replica is healthy, the stale replica reference is purged from the metadata record.
- Stale references that cannot be re-replicated (e.g., no eligible nodes in the required region) remain flagged and are surfaced in the Tenant Observability dashboard as a placement warning.

### 5. Orphaned Blob Data (No Metadata)

In rare failure scenarios (a node crash mid-write, a metadata write succeeding but the data write failing), a storage node may hold data for which no corresponding metadata record exists in the coordination layer. This data is never accessible — there is no key to retrieve it by — but it consumes disk space.

**GC rules:**
- Each node runs a periodic reconciliation scan (default: once per epoch) comparing locally stored blob files against the metadata records it has received via gossip.
- Any locally stored blob that has no corresponding metadata record after a grace period of 48 hours (to account for metadata propagation lag) is flagged as orphaned.
- Orphaned blobs are reported to the coordination layer, which cross-references them against the chain. If no metadata record exists on-chain, the blob is deleted locally.
- The 48-hour grace period prevents newly written blobs from being incorrectly GC'd before their metadata has fully propagated.

### 6. Expired Presigned URLs and Credentials

Presigned URLs and temporary credentials expire automatically by their embedded expiry timestamp — no GC is needed to revoke them, they become invalid at expiry. However:

- Issued presigned URL records are retained in the audit log for the standard audit retention period.
- Expired revocation list entries (for credentials that have been revoked and whose original expiry has also passed) may be pruned from the active revocation list after a defined holding period (e.g., 90 days past expiry) to keep the revocation list compact.

---

## GC Scheduling and Safety

### Safety Invariants

GC processes must never violate these invariants:

1. A blob that is readable via the API is never physically deleted.
2. A blob with an active immutability lock or legal hold is never deleted by any GC process.
3. A blob whose region assignment cannot currently be satisfied (not enough nodes) is never deleted for placement reasons.
4. GC never runs on a blob that has a pending re-replication operation — GC waits until replication is stable.

### Prioritization

GC tasks are background operations and are throttled to avoid consuming node I/O or network bandwidth needed for live client operations.

| Priority | GC Task |
|---|---|
| High | Purging replicas from slashed/terminated nodes (frees their disk for re-replication) |
| Medium | Physical deletion of soft-deleted blobs past recovery window |
| Medium | Incomplete multipart upload cleanup |
| Low | Stale metadata reconciliation |
| Low | Orphaned blob reconciliation |

### GC and Billing

- Storage is billed until physical deletion is confirmed — not at the time of soft delete or at the time GC is queued.
- Incomplete multipart upload parts are billed for the time they exist, until GC removes them.
- The billing system and the GC system share a consistent view of what is physically present to avoid charging for data that has already been deleted.

---

## Tenant-Visible GC Controls

- **Multipart upload TTL**: Configurable per bucket (see above).
- **Lifecycle rules**: Define version expiry and object TTL (see Data Management requirements).
- **List incomplete uploads**: Tenants can query in-progress multipart uploads and manually abort them at any time.
- **GC status**: The observability dashboard shows counts of objects pending physical deletion and the estimated time until they are purged.
