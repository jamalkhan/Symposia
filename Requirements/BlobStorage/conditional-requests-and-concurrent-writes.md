# Conditional Requests and Concurrent Writes

## Overview

Conditional requests allow clients to make reads and writes that are contingent on the current state of an object. They are the primary mechanism for safe concurrent access — preventing two processes from overwriting each other's changes and enabling efficient client-side caching. Without them, multi-process or multi-client architectures must implement their own locking, which is fragile and error-prone.

---

## ETags

Every object has an **ETag** — a short string that uniquely identifies the current version of the object's content.

- The ETag is the SHA-256 hash of the object's plaintext content, hex-encoded, quoted (e.g., `"a3f1b2c4d5..."`).
- For multipart uploads, the ETag is a hash of the concatenated part hashes, following the S3 multipart ETag convention.
- The ETag changes whenever the object's content changes. It does not change when only metadata or tags are updated.
- The ETag is returned in the `ETag` response header on every GET, HEAD, and PUT response.
- The ETag is stored in the object's metadata record and is queryable independently of the object content.

**Note on encryption**: Because clients encrypt content before upload, the ETag is the hash of the **ciphertext** received by the platform, not the plaintext. Clients who need a content-hash of the plaintext should compute it themselves before encryption and store it as user-defined metadata.

---

## Conditional Headers

The following conditional headers are supported on GET, HEAD, PUT, and DELETE operations:

| Header | Description |
|---|---|
| `If-Match` | Proceed only if the object's current ETag matches the provided value. |
| `If-None-Match` | Proceed only if the object's current ETag does **not** match the provided value. |
| `If-Modified-Since` | (GET/HEAD only) Proceed only if the object was modified after the provided date. |
| `If-Unmodified-Since` | Proceed only if the object was **not** modified after the provided date. |

When a condition fails, the gateway returns:
- `304 Not Modified` for GET/HEAD with `If-None-Match` or `If-Modified-Since`.
- `412 Precondition Failed` for PUT/DELETE with any failed condition.

Multiple conditions may be combined on a single request. All conditions must be satisfied for the request to proceed.

---

## Conditional GET (Client-Side Caching)

### If-None-Match

Used by clients to revalidate a cached copy. The client sends the ETag of its cached version:

```
GET /bucket/object.jpg
If-None-Match: "a3f1b2c4d5..."
```

If the object has not changed (ETag still matches), the gateway returns `304 Not Modified` with no body — saving the cost of re-transferring the content. If the object has changed, the gateway returns `200 OK` with the new content and ETag.

### If-Modified-Since

Used when a client has a cached copy and knows its last-modified timestamp but not its ETag:

```
GET /bucket/object.jpg
If-Modified-Since: Mon, 01 Jan 2026 00:00:00 GMT
```

Returns `304 Not Modified` if the object has not been modified since that timestamp.

### Cache-Control and Last-Modified

The gateway returns `Last-Modified` and `Cache-Control` headers on all GET responses, allowing HTTP intermediaries (CDNs, proxies, browsers) to cache objects appropriately. The `Cache-Control` value is configurable per object via user-defined metadata (`cache-control` key).

---

## Conditional PUT (Safe Concurrent Writes)

Conditional PUTs are the mechanism for safe concurrent modification. They prevent lost updates when multiple processes read and then write the same object.

### Create-Only Write (If-None-Match: \*)

Succeeds only if the object does **not** exist. Used to guarantee that an object is created exactly once:

```
PUT /bucket/config.json
If-None-Match: *
```

- Returns `201 Created` if the object did not exist and was created.
- Returns `412 Precondition Failed` if the object already exists.

Use cases: initializing a config file, claiming a unique resource name, implementing distributed locks.

### Compare-and-Swap Write (If-Match)

Succeeds only if the object's current ETag matches the provided value. Used to implement optimistic concurrency control:

```
PUT /bucket/config.json
If-Match: "a3f1b2c4d5..."
```

- Returns `200 OK` (or `201`) if the ETag matched and the write succeeded.
- Returns `412 Precondition Failed` if the ETag did not match (the object was modified by someone else since you last read it).

**Typical pattern for safe update**:
1. GET the object, note the ETag from the response header.
2. Modify the content locally.
3. PUT the modified content with `If-Match: <etag-from-step-1>`.
4. If `412`, another writer changed the object — go back to step 1 and retry.

This pattern is the standard way to safely update a shared object without a distributed lock.

### Conditional DELETE

DELETE also supports `If-Match` and `If-None-Match`. A common use case is deleting an object only if it hasn't changed since you last observed it:

```
DELETE /bucket/lock-file
If-Match: "a3f1b2c4d5..."
```

Returns `412` if the object was modified (preventing deletion of an object another process has updated).

---

## Concurrent Write Behavior Without Conditions

When two clients write to the same key simultaneously **without** conditional headers, the outcome is last-writer-wins (see [Write Quorum and Consistency](./write-quorum-and-consistency.md)). Neither write is rejected — they race, and one wins. The losing write is silently discarded.

For any workload where two processes might write the same key, **always use conditional writes**. The platform does not provide automatic locking or serialization for unconditional concurrent writes.

---

## Atomicity Guarantee

A conditional PUT is atomic at the gateway level: the condition check and the write happen as a single operation. Two concurrent conditional PUTs with `If-None-Match: *` on the same key will result in exactly one succeeding and one returning `412`. There is no race window between the check and the write.

This atomicity is enforced by the metadata index, which uses an optimistic locking mechanism on the object's metadata record. The quorum write to storage nodes is initiated only after the metadata lock is acquired.

---

## S3 and Azure Compatibility

The conditional header behavior is compatible with:
- **S3**: `If-Match`, `If-None-Match`, `If-Modified-Since`, `If-Unmodified-Since` on GET, HEAD, PUT, DELETE, and CopyObject.
- **Azure Blob**: `If-Match`, `If-None-Match`, `If-Modified-Since`, `If-Unmodified-Since` on Get Blob, Put Blob, Delete Blob, and Copy Blob.

Existing code using conditional requests against S3 or Azure will work without modification.
