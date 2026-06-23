# Error Catalog

## Overview

Every error the API returns must be predictable, machine-readable, and actionable. Clients must be able to programmatically distinguish between a retryable error (node temporarily unavailable) and a permanent error (object does not exist), and between an authorization failure and a quota failure. This file defines the error format for the platform-native API and documents how S3 and Azure error codes are mapped for compatibility interfaces.

---

## Native API Error Format

All errors from the platform-native API are returned as JSON with a consistent structure:

```json
{
  "error": {
    "code": "ObjectNotFound",
    "message": "The object 'my-bucket/path/to/file.jpg' does not exist.",
    "request_id": "req_01j9xkp2abc",
    "timestamp": "2026-06-23T14:32:00.000Z",
    "details": {}
  }
}
```

| Field | Description |
|---|---|
| `code` | A stable, PascalCase string identifier. Clients should switch on this field, not on the HTTP status code or message. |
| `message` | A human-readable description. May change between API versions; do not parse it programmatically. |
| `request_id` | A unique ID for this request. Include this in any support ticket or bug report. |
| `timestamp` | UTC timestamp of the error. |
| `details` | Optional structured data providing additional context specific to the error type (e.g., which quota was exceeded and by how much). |

---

## Error Codes by HTTP Status

### 400 Bad Request

| Code | Meaning | Retryable |
|---|---|---|
| `InvalidRequest` | The request is malformed — missing required field, invalid JSON, etc. | No |
| `InvalidBucketName` | The bucket name does not meet naming requirements. | No |
| `InvalidObjectKey` | The object key contains invalid characters or exceeds length limits. | No |
| `InvalidRange` | The `Range` header specifies a range outside the object's bounds. | No |
| `InvalidPartOrder` | Multipart complete request lists parts in invalid order. | No |
| `MissingContentLength` | A PUT request did not include a `Content-Length` header. | No |
| `TooManyTags` | The request would result in more than 10 tags on the object. | No |
| `InvalidTagKey` | A tag key uses reserved prefix `sys:` or contains invalid characters. | No |
| `EntityTooLarge` | Object size exceeds the 5 TB maximum. | No |
| `InvalidCopySource` | The copy source path is malformed or refers to an inaccessible object. | No |

### 401 Unauthorized

| Code | Meaning | Retryable |
|---|---|---|
| `AuthenticationRequired` | No credential was provided. | No |
| `InvalidCredential` | The credential is malformed or cannot be parsed. | No |
| `CredentialExpired` | The credential's expiry timestamp has passed. | No — obtain a new credential |
| `InvalidPresignedUrl` | The presigned URL signature is invalid or the URL has been tampered with. | No |
| `PresignedUrlExpired` | The presigned URL has passed its expiry time. | No — generate a new URL |

### 403 Forbidden

| Code | Meaning | Retryable |
|---|---|---|
| `AccessDenied` | The credential does not have permission for this operation. | No |
| `ReadOnlyCredential` | A write operation was attempted with a read-only credential. | No |
| `ScopeMismatch` | The credential is scoped to a different bucket or key. | No |
| `CredentialRevoked` | The credential has been explicitly revoked. | No — issue a new credential |
| `PublicAccessBlocked` | The requested public access is blocked by the bucket's `public_access_block` setting. | No |
| `AccountSuspended` | The tenant account is suspended (non-payment or policy violation). | No |

### 404 Not Found

| Code | Meaning | Retryable |
|---|---|---|
| `BucketNotFound` | The specified bucket does not exist in this tenant account. | No |
| `ObjectNotFound` | The specified object key does not exist in the bucket. | No |
| `VersionNotFound` | The specified version ID does not exist for this object. | No |
| `UploadNotFound` | The specified multipart upload ID does not exist or has expired. | No |
| `SubscriptionNotFound` | The specified event subscription does not exist. | No |

### 405 Method Not Allowed

| Code | Meaning | Retryable |
|---|---|---|
| `MethodNotAllowed` | The HTTP method is not supported on this endpoint. | No |

### 409 Conflict

| Code | Meaning | Retryable |
|---|---|---|
| `BucketAlreadyExists` | A bucket with this name already exists in the tenant account. | No |
| `BucketNotEmpty` | Cannot delete a bucket that contains objects. | No — delete objects first |
| `ObjectImmutable` | The object is within an immutability lock period and cannot be modified or deleted. | No — wait for lock to expire |
| `LegalHoldActive` | A legal hold is preventing this deletion. | No — release the hold first |
| `VersioningCannotDisable` | Versioning cannot be disabled once enabled; only suspended. | No |

### 412 Precondition Failed

| Code | Meaning | Retryable |
|---|---|---|
| `PreconditionFailed` | A conditional request header (If-Match, If-None-Match, etc.) condition was not satisfied. | No — re-evaluate the condition |

### 429 Too Many Requests

| Code | Meaning | Retryable |
|---|---|---|
| `RateLimitExceeded` | The request rate limit for this credential or IP has been exceeded. | Yes — respect `Retry-After` header |
| `SearchRateLimitExceeded` | The metadata search rate limit has been exceeded. | Yes — respect `Retry-After` header |

All 429 responses include a `Retry-After` header indicating the number of seconds to wait before retrying.

### 500 Internal Server Error

| Code | Meaning | Retryable |
|---|---|---|
| `InternalError` | An unexpected error occurred on the platform. The `request_id` should be included in any support report. | Yes — with exponential backoff |

### 503 Service Unavailable

| Code | Meaning | Retryable |
|---|---|---|
| `ServiceUnavailable` | The service is temporarily unavailable (gateway overloaded, maintenance, or deployment in progress). | Yes — respect `Retry-After` |
| `QuorumNotReached` | Insufficient nodes confirmed the write within the timeout. The write was not committed. | Yes — retry the full upload |
| `NoEligibleNodes` | No eligible nodes could be found to satisfy the request's region, tier, or fault domain requirements. | Yes — after delay; conditions may change |
| `ObjectTemporarilyUnavailable` | All replicas of the requested object are temporarily offline. | Yes — respect `Retry-After` |

### 507 Insufficient Storage

| Code | Meaning | Retryable |
|---|---|---|
| `AccountQuotaExceeded` | The write would exceed the account-level storage quota. | No — increase quota or delete objects |
| `BucketQuotaExceeded` | The write would exceed the bucket-level storage quota. | No — increase quota or delete objects |
| `AccountSuspendedNoWrites` | The account's credit balance is zero; writes are suspended. | No — top up credit balance |

`details` for quota errors includes:

```json
{
  "details": {
    "quota_type": "bucket",
    "limit_bytes": 107374182400,
    "used_bytes": 107374182399,
    "incoming_bytes": 2097152
  }
}
```

---

## S3 Interface Error Mapping

When errors occur on the S3-compatible interface, they are returned in S3's XML error format:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<Error>
  <Code>NoSuchKey</Code>
  <Message>The specified key does not exist.</Message>
  <Key>my-bucket/path/to/file.jpg</Key>
  <RequestId>req_01j9xkp2abc</RequestId>
</Error>
```

| Native Code | S3 Code |
|---|---|
| `ObjectNotFound` | `NoSuchKey` |
| `BucketNotFound` | `NoSuchBucket` |
| `AccessDenied` | `AccessDenied` |
| `AuthenticationRequired` | `MissingSecurityHeader` |
| `InvalidCredential` | `InvalidAccessKeyId` |
| `CredentialExpired` | `ExpiredToken` |
| `QuorumNotReached` | `ServiceUnavailable` |
| `AccountQuotaExceeded` | `QuotaExceeded` (non-standard; also returned as `EntityTooLarge` for compatibility) |
| `PreconditionFailed` | `PreconditionFailed` |
| `BucketAlreadyExists` | `BucketAlreadyOwnedByYou` |
| `ObjectImmutable` | `ObjectLockConfigurationNotAllowedError` |
| `RateLimitExceeded` | `SlowDown` |
| `InternalError` | `InternalError` |

---

## Azure Interface Error Mapping

Azure errors are returned as JSON with the Azure error format:

```json
{
  "error": {
    "code": "BlobNotFound",
    "message": "The specified blob does not exist."
  }
}
```

| Native Code | Azure Code |
|---|---|
| `ObjectNotFound` | `BlobNotFound` |
| `BucketNotFound` | `ContainerNotFound` |
| `AccessDenied` | `AuthorizationFailure` |
| `AuthenticationRequired` | `AuthenticationFailed` |
| `QuorumNotReached` | `ServerBusy` |
| `AccountQuotaExceeded` | `AccountIsDisabled` |
| `PreconditionFailed` | `ConditionNotMet` |
| `BucketAlreadyExists` | `ContainerAlreadyExists` |
| `RateLimitExceeded` | `ServerBusy` |
| `InternalError` | `InternalError` |

---

## Retry Guidance

Clients should implement **exponential backoff with jitter** for all retryable errors:

- Initial delay: 1 second.
- Backoff multiplier: 2×.
- Maximum delay: 60 seconds.
- Jitter: ±25% of computed delay to prevent thundering herd.
- Maximum retries: 5 before surfacing the error to the application.

For `429 Too Many Requests` and `503 Service Unavailable`, always respect the `Retry-After` header value rather than computing a backoff — the platform knows the appropriate wait time.

All platform SDKs implement this retry policy automatically. Applications using raw HTTP clients should implement it themselves.
