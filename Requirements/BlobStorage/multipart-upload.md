# Multipart Upload

## Overview

Multipart upload is the required mechanism for uploading objects larger than 5 GB, and the recommended mechanism for any object above ~100 MB. It allows a large upload to be broken into independently uploaded parts, any of which can be retried without re-sending the entire file. Because the platform streams blobs to storage nodes during upload, multipart is also the primitive used by the gateway internally for all fan-out operations on large objects.

The multipart upload API is compatible with the S3 multipart upload interface, enabling existing S3 SDKs and tools to work without modification.

---

## Limits

| Parameter | Value |
|---|---|
| Maximum object size | 5 TB |
| Maximum number of parts | 10,000 |
| Minimum part size (except last part) | 5 MB |
| Maximum part size | 5 GB |
| Last part minimum size | 1 byte (no minimum) |
| Upload ID lifetime (before expiry/GC) | 7 days (configurable per bucket; see [Garbage Collection](./garbage-collection.md)) |
| Maximum concurrent in-progress uploads per bucket | No hard limit (subject to quota) |

If a part is smaller than the minimum size and is not the last part, the `CompleteMultipartUpload` call is rejected with `InvalidPartSize`.

---

## Lifecycle

A multipart upload proceeds in four steps. All four must complete successfully for the object to become visible.

### 1. Initiate

```
POST /buckets/{bucket}/{key}?uploads
```

Returns an `UploadId` that identifies this upload session. The upload session is tied to the key, bucket, and initiating credential. All subsequent operations for this upload must supply the `UploadId`.

The initiation call accepts the same metadata headers as a regular PUT (content type, user-defined metadata, region assignment, performance tier, tags). These are recorded at initiation and applied when the upload completes.

Response:
```json
{
  "upload_id": "up_01j9xkp2abc",
  "bucket": "my-bucket",
  "key": "data/large-file.parquet",
  "initiated_at": "2026-06-23T10:00:00Z",
  "expires_at": "2026-06-30T10:00:00Z"
}
```

### 2. Upload Parts

```
PUT /buckets/{bucket}/{key}?partNumber={n}&uploadId={id}
```

Parts are numbered from **1** to **10,000** (inclusive). Parts do not need to be uploaded in order — part 7 may be uploaded before part 3. The gateway fans out each part to the target storage nodes immediately; it does not buffer the full object before forwarding.

Each successful part upload returns an `ETag` for that part. Clients must retain all part ETags and part numbers to use in the `CompleteMultipartUpload` call.

A part may be re-uploaded (same part number, new data) before the upload is completed. Only the most recent version of a part number is retained. Re-uploading a part overwrites the previous version; the old part data is discarded.

#### Part Integrity

Clients may include a `Content-MD5` header on each part upload. The gateway verifies the MD5 on receipt and rejects the part with `400 Bad Request` if the hash does not match. This prevents silent corruption during transmission.

For stronger integrity, clients may include a `x-checksum-sha256` header with the SHA-256 hash of the part. The gateway rejects the part if the hash does not match.

### 3. Complete

```
POST /buckets/{bucket}/{key}?uploadId={id}

{
  "parts": [
    { "part_number": 1, "etag": "\"a3f1b2...\"" },
    { "part_number": 2, "etag": "\"c4d5e6...\"" },
    ...
  ]
}
```

The parts list must:
- Include every part that will form the final object (missing parts are not included — the assembly is exactly the specified list).
- Be ordered by ascending part number (part 1 first, last part last).
- Contain the exact ETag returned by the server for each part (client-provided ETags are not accepted).

On completion:
1. The gateway validates that all referenced part ETags match what was recorded during upload.
2. It validates that no non-final part is smaller than the 5 MB minimum.
3. It assembles the object's final ETag as a hash of the concatenated part ETags (following the S3 multipart ETag convention: `MD5(hex(part1_etag) + hex(part2_etag) + ...)-{part_count}`).
4. It commits the assembled metadata record. The object becomes visible in LIST results immediately after this commit.
5. The individual part records are marked for cleanup by GC.

Response: `200 OK` with the final object ETag and last-modified timestamp.

If the `CompleteMultipartUpload` call is interrupted (client disconnects mid-request), the gateway retries the commit internally. A second `CompleteMultipartUpload` call with the same parts list is idempotent — it returns the existing committed object.

### 4. Abort (Optional)

```
DELETE /buckets/{bucket}/{key}?uploadId={id}
```

Cancels the upload and schedules all uploaded parts for immediate deletion. After abort, the `UploadId` is invalid and any further operations on it return `404`.

Abort may be called at any time before `CompleteMultipartUpload`. It is the correct cleanup action when an upload fails or is abandoned by the client.

If not explicitly aborted, incomplete uploads are cleaned up automatically after the bucket's multipart TTL (see [Garbage Collection](./garbage-collection.md)).

---

## Listing In-Progress Uploads

```
GET /buckets/{bucket}?uploads[&prefix={prefix}][&max-uploads={n}]
```

Returns all in-progress multipart uploads for the bucket, optionally filtered by key prefix. Useful for auditing and cleanup.

```
GET /buckets/{bucket}/{key}?uploadId={id}&parts
```

Returns the list of parts uploaded so far for a specific upload session, including their part numbers, ETags, sizes, and upload timestamps.

---

## Write Quorum for Parts

Each part upload follows the same write quorum rules as a single-object PUT. The quorum is determined by the object's region assignment and required copy count (see [Write Quorum and Consistency](./write-quorum-and-consistency.md)). A part upload is not acknowledged to the client until the quorum of nodes confirms receipt of that part.

This means individual part failures can be retried without re-uploading other parts. If part 7 fails quorum (e.g., the network becomes degraded mid-upload), the client retries only part 7.

---

## Concurrent Uploads to the Same Key

Multiple multipart uploads for the same key may be in progress simultaneously (each with a different `UploadId`). The first to complete `CompleteMultipartUpload` wins; subsequent completions for the same key follow the last-writer-wins rule (see [Write Quorum and Consistency](./write-quorum-and-consistency.md)).

To prevent accidental overwrites when two processes are uploading to the same key, use `If-None-Match: *` on the `CompleteMultipartUpload` call to make the completion fail if the key already exists (see [Conditional Requests and Concurrent Writes](./conditional-requests-and-concurrent-writes.md)).

---

## Quota Interaction

Uploaded parts do not count toward storage quota until `CompleteMultipartUpload` is called. However, they do count toward billing from the moment they are stored on nodes (see [Garbage Collection](./garbage-collection.md) for cleanup of abandoned parts). This distinction is important: an incomplete upload does not block quota but does incur storage costs.

---

## S3 and Azure Compatibility

### S3

The S3 multipart upload API is fully supported:
- `CreateMultipartUpload` → `POST /{bucket}/{key}?uploads`
- `UploadPart` → `PUT /{bucket}/{key}?partNumber=N&uploadId=...`
- `CompleteMultipartUpload` → `POST /{bucket}/{key}?uploadId=...` with XML body
- `AbortMultipartUpload` → `DELETE /{bucket}/{key}?uploadId=...`
- `ListMultipartUploads` → `GET /{bucket}?uploads`
- `ListParts` → `GET /{bucket}/{key}?uploadId=...&parts`

### Azure

Azure Blob Storage uses a different model (block blobs with `PutBlock` / `PutBlockList`). The platform maps this to the internal multipart model:
- `PutBlock` → equivalent to `UploadPart`; the block ID is stored and associated with the upload session.
- `PutBlockList` → equivalent to `CompleteMultipartUpload`; specifies the committed block list in order.
- `GetBlockList` → equivalent to `ListParts`.

---

## Error Cases

| Scenario | Error Returned |
|---|---|
| `CompleteMultipartUpload` with an ETag that doesn't match the server's record | `InvalidPart` |
| Part smaller than 5 MB (except last) | `InvalidPartSize` |
| More than 10,000 parts | `TooManyParts` |
| `UploadId` not found or expired | `UploadNotFound` |
| Parts list out of order in `CompleteMultipartUpload` | `InvalidPartOrder` |
| Quota would be exceeded by the completed object | `AccountQuotaExceeded` or `BucketQuotaExceeded` (checked at completion time) |
