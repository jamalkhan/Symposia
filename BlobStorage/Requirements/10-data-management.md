# Data Management

## Overview

Beyond storing and retrieving blobs, the system must support the full lifecycle of data: large file ingestion, partial access, versioning, deletion semantics, and integrity guarantees. These features ensure the system is practical for real-world workloads.

## Large File Handling

- **Multipart uploads**: Files larger than a configurable threshold (e.g., 100 MB) may be uploaded in parts. Parts are uploaded independently and assembled server-side or client-side before commit.
- **Resumable uploads**: A failed or interrupted upload can be resumed from the last successfully uploaded part without restarting from zero.
- **Range requests**: Clients may request a specific byte range of a blob (`Range` header in S3/HTTP semantics). This is required for efficient streaming, seeking in large media files, and parallel download.
- **Chunked transfer**: The upload path must not require the full blob to be buffered in memory on the gateway before forwarding to storage nodes.

## Versioning

- Buckets may optionally enable **versioning**. When enabled, every `PUT` to an existing key creates a new version rather than overwriting the previous one.
- All versions of a blob are accessible and independently retrievable by version ID.
- The current (latest) version is returned by default; clients may request a specific version.
- Versioning may be suspended (new writes overwrite the current version, existing versions are retained) or disabled (existing versions are deleted, future writes are non-versioned).

## Delete Semantics

- **Soft delete**: A deleted blob is marked as deleted (tombstoned) and becomes inaccessible via normal reads, but is retained for a configurable retention period before permanent removal.
- **Hard delete**: Permanent, immediate deletion with no recovery window.
- Versioned buckets: deleting a key inserts a delete marker; the blob and all its versions remain until explicitly purged.
- **Immutability**: Buckets or individual blobs may be marked immutable for a defined retention period. Immutable blobs cannot be deleted or overwritten until the retention period expires, even by the tenant owner.

## Content Addressing and Integrity

- Every blob is assigned a content hash (SHA-256 or a content identifier, CID) at ingest time.
- The hash is stored in metadata and returned to clients on upload for local verification.
- Clients may supply an expected hash on upload; the server rejects the upload if the hash does not match the received content.
- Reads include a content hash in the response for clients to verify end-to-end integrity.

## Metadata

- Each blob carries system metadata (content type, size, hash, upload timestamp, last-modified, version ID, region assignments, replica list).
- User-defined metadata: tenants may attach arbitrary key-value pairs to any blob (subject to size limits).
- Metadata is separately queryable without downloading blob content (equivalent to `HeadObject` in S3 semantics).

## Lifecycle Policies

- Tenants may define **lifecycle rules** on buckets or prefixes:
  - Transition blobs to reduced-cost storage tiers after a defined age.
  - Automatically expire (delete) blobs after a defined retention period.
  - Purge non-current versions after a defined number of days.
- Lifecycle rules are evaluated by the system periodically (e.g., daily) and executed automatically.

## Encryption and Key Management for Data

- The system supports **at-rest encryption** at the node storage layer.
- **Client-side encryption** allows tenants to encrypt data before upload; the system stores ciphertext and has no knowledge of plaintext or encryption keys.
- **Server-side encryption with tenant-managed keys** (analogous to AWS SSE-C) allows tenants to provide a key per request; the server encrypts, stores the ciphertext, and discards the key immediately.
- Key management infrastructure (KMS integration) is a future consideration; the architecture must not preclude it.
