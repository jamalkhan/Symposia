# Write Quorum and Consistency

## Overview

Two closely related decisions govern how the system behaves when data is written and immediately read back: **write quorum** (how many nodes must confirm a write before the client sees success) and **consistency model** (what a client is guaranteed to see after a successful write). These decisions directly affect the reliability guarantees the platform can advertise and the behavior application developers must design around.

---

## Write Quorum

### Definition

The **write quorum** is the minimum number of storage nodes that must durably acknowledge receipt of a blob before the gateway returns HTTP 201 to the client. The remaining replicas are written asynchronously after the client's request has already returned.

A node "durably acknowledges" a write only after the data has been flushed to persistent storage (`fdatasync` or equivalent) — an in-memory acknowledgement is not sufficient. This is required to ensure that a node crash immediately after acknowledgement does not cause data loss.

### Quorum by Copy Count

| Region Assignment | Total Copies Required | Write Quorum | Async Remainder |
|---|---|---|---|
| Zero regions (global) | 4 | 3 | 1 |
| One region | 5 | 3 | 2 |
| Two regions | 6 | 4 | 2 |
| Three regions | 7 | 4 | 3 |
| Four+ regions | 2N | N+1 | N-1 |

**Rationale**: Quorum is set at a majority of the total required copies in all cases. A majority quorum means that even if the async remainder never completes (e.g., due to node failures), the written data is held by enough nodes to survive any single-node failure and be served to clients.

### Quorum and Region Constraints

For region-constrained blobs, the quorum must include at least one node from each **required** region before returning success. Specifically:

- **One region**: At least 2 of the 3 quorum confirmations must come from nodes in the target region. The third may be from any region.
- **Two regions**: At least 1 confirmation must come from each of the two target regions. The remaining 2 quorum confirmations may be from any region.
- **Three regions**: At least 1 confirmation per target region. The fourth quorum confirmation may be from any region.

This ensures that a write is never acknowledged when the data exists only in overflow regions, which would violate the tenant's stated placement constraint.

### Write Timeout

The gateway waits up to **30 seconds** for quorum to be reached. If quorum is not achieved within this window:
- The write fails with `503 Service Unavailable`.
- The gateway instructs all nodes that received partial data to discard it.
- The ETag is not committed to the metadata index.
- The client must retry the full upload.

The 30-second timeout is configurable at the gateway level. Very large blobs on slow networks may require a longer timeout, which can be requested by enterprise tenants.

### Async Replication

After quorum is reached and the client receives HTTP 201, the gateway (or the nodes themselves via gossip) continues replicating to the remaining target nodes in the background. The blob transitions from "quorum-replicated" to "fully replicated" once all copies are confirmed.

The blob is immediately readable at full fidelity after quorum is reached — clients do not need to wait for full replication. The read routing layer is aware of which nodes have confirmed the write and will not route reads to nodes still awaiting their async copy.

---

## Consistency Model

The consistency model defines what a client is guaranteed to observe after a successful write. Different operations have different consistency guarantees, which is the norm across all distributed storage systems.

### Read-After-Write Consistency (New Objects)

After a successful PUT of a **new key** (a key that did not previously exist), any subsequent GET or HEAD on that key from any gateway instance is guaranteed to return the new data.

This guarantee holds because:
- The metadata index is not updated until quorum is reached.
- The metadata commit is synchronous before HTTP 201 is returned.
- All gateway instances read from the same metadata index.

### Eventual Consistency (Overwrites and Deletes)

After overwriting an existing key or deleting a key, **list operations** may temporarily reflect the old state for up to **60 seconds**. GET and HEAD on the specific key are strongly consistent — they will return the new state immediately.

This mirrors the consistency model of AWS S3 (prior to their 2020 strong consistency upgrade) and is sufficient for the majority of workloads. The 60-second window exists because the metadata index uses a propagation model across distributed index nodes; the list cache lags slightly behind committed writes.

A future upgrade to strong consistency for list operations is a governance decision once the metadata index architecture supports it without a performance penalty.

### Summary Table

| Operation | Consistency Guarantee | Max Lag |
|---|---|---|
| GET / HEAD after new PUT | Strong (read-after-write) | 0 |
| GET / HEAD after overwrite | Strong | 0 |
| GET / HEAD after delete | Strong | 0 |
| LIST after new PUT | Strong | 0 |
| LIST after overwrite | Eventual | ≤ 60 seconds |
| LIST after delete | Eventual | ≤ 60 seconds |
| LIST after multipart complete | Strong | 0 |

### Implications for Application Developers

- **Safe pattern**: Write a new key, then immediately read it back. Guaranteed to see the new data.
- **Safe pattern**: Upload a file, then list the bucket to confirm it appears. Guaranteed for new objects.
- **Caution**: Delete a file and immediately list to confirm it's gone — may show the old entry for up to 60 seconds. Design around this with conditional checks on the specific key rather than relying on list absence.
- **Caution**: Overwrite a key and expect list metadata (size, last-modified) to update immediately — may lag. Use HEAD on the specific key to confirm current state.

### Multipart Uploads

A multipart upload is not visible in LIST results until `CompleteMultipartUpload` is called and acknowledged. Individual parts are not addressable or listable while the upload is in progress. Once `CompleteMultipartUpload` succeeds, the resulting object is immediately consistent (same guarantee as a new PUT).

---

## Conflict Resolution for Concurrent Writes

When two clients write to the same key simultaneously (a race condition), the outcome is determined by **last-writer-wins**: whichever write achieves quorum and commits its metadata record last becomes the current version.

The gateway uses a monotonic timestamp on the metadata commit to determine ordering. In the event of a true tie (two commits with the same millisecond timestamp), the commit with the lexicographically greater ETag wins — this is deterministic and ensures all gateway instances agree on the outcome.

For workloads where concurrent write conflicts must be prevented, use conditional writes (see [Conditional Requests and Concurrent Writes](./conditional-requests-and-concurrent-writes.md)).
