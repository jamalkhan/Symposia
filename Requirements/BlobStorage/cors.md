# Cross-Origin Resource Sharing (CORS)

## Overview

CORS is required for any web application that reads or writes blobs directly from a browser. Without CORS configuration, browsers will block cross-origin requests to the storage API, making the platform unusable for client-side web workloads. CORS rules are configured per bucket and applied by the gateway on all responses.

---

## How CORS Works in This Context

When a browser makes a cross-origin request (e.g., a React app at `app.example.com` fetching a blob from `gateway.network.example`), it first sends a preflight `OPTIONS` request to check whether the server permits the cross-origin access. The gateway responds with the appropriate CORS headers based on the bucket's configured rules. If the preflight passes, the browser sends the actual request.

The gateway handles all CORS logic. Storage nodes are not exposed to browsers directly and do not implement CORS.

---

## CORS Configuration Per Bucket

Each bucket may have zero or more CORS rules. Rules are evaluated in order; the first matching rule applies. If no rule matches a given request, no CORS headers are returned and the browser will block the cross-origin request.

### CORS Rule Structure

```json
{
  "id": "rule-1",
  "allowed_origins": ["https://app.example.com", "https://staging.example.com"],
  "allowed_methods": ["GET", "PUT", "DELETE", "HEAD"],
  "allowed_headers": ["Content-Type", "Authorization", "x-amz-*", "x-ms-*"],
  "expose_headers": ["ETag", "Content-Length", "x-blob-region"],
  "max_age_seconds": 3600
}
```

| Field | Description |
|---|---|
| `id` | Optional human-readable identifier for the rule. |
| `allowed_origins` | List of origins permitted. Supports exact match (`https://example.com`) and wildcard (`*`). Wildcard permits any origin. |
| `allowed_methods` | HTTP methods permitted cross-origin. Valid values: `GET`, `PUT`, `POST`, `DELETE`, `HEAD`. |
| `allowed_headers` | Request headers the browser is allowed to send. Supports prefix wildcard (e.g., `x-amz-*` matches all headers starting with `x-amz-`). |
| `expose_headers` | Response headers that the browser JavaScript is allowed to read. By default browsers only expose a small set of "safe" headers. |
| `max_age_seconds` | How long the browser may cache a preflight response (seconds). Reduces preflight round-trips for repeated requests. Maximum: 86400 (24 hours). |

### Limits

- Maximum 10 CORS rules per bucket.
- Maximum 100 origins per rule.
- Maximum 100 allowed headers per rule.
- Maximum 100 expose headers per rule.

---

## Wildcard Origin Behavior

When `allowed_origins` contains `"*"`, the gateway returns `Access-Control-Allow-Origin: *` for matching requests. This permits any origin to access the bucket cross-origin.

**Important security note**: A wildcard origin combined with a write method (`PUT`, `DELETE`) means any website can write to or delete from the bucket using the visiting user's credentials. This should only be used for fully public buckets with no sensitive data. The gateway will emit a warning in the tenant's security log whenever a wildcard-origin CORS rule is applied to a non-public bucket.

Credentials (cookies, Authorization headers) cannot be sent with wildcard-origin CORS requests — this is a browser security constraint (`credentials: 'include'` requires an explicit origin, not `*`).

---

## Gateway CORS Response Headers

For a matching preflight (`OPTIONS`) request, the gateway returns:

```
HTTP/1.1 204 No Content
Access-Control-Allow-Origin: https://app.example.com
Access-Control-Allow-Methods: GET, PUT, DELETE, HEAD
Access-Control-Allow-Headers: Content-Type, Authorization
Access-Control-Max-Age: 3600
Vary: Origin
```

For a matching actual request, the gateway adds to the response:

```
Access-Control-Allow-Origin: https://app.example.com
Access-Control-Expose-Headers: ETag, Content-Length
Vary: Origin
```

The `Vary: Origin` header is always included when CORS headers are present to ensure CDN caches do not incorrectly serve a CORS response to a different origin.

---

## Preflight Caching

The `max_age_seconds` value instructs the browser to cache the preflight result for that duration. During that window, the browser skips the preflight for subsequent requests with the same method and headers to the same endpoint.

Recommended values:
- Development: 0 (no caching — see every preflight for debugging).
- Production: 3600 (1 hour) to 86400 (24 hours).

---

## CORS and Presigned URLs

Presigned URLs are typically used for direct browser uploads or downloads. CORS rules apply to presigned URL requests in the same way as regular authenticated requests. The origin of the browser making the presigned URL request must match the bucket's CORS configuration.

For public presigned URL use cases (e.g., direct browser file upload), configure the bucket's CORS rules to allow the origin of the uploading page.

---

## CORS Management API

```
GET    /buckets/{bucket}/cors           Get current CORS configuration
PUT    /buckets/{bucket}/cors           Replace entire CORS configuration
DELETE /buckets/{bucket}/cors           Remove all CORS rules (CORS disabled)
```

CORS configuration changes take effect within 30 seconds across all gateway instances.

---

## S3 and Azure Compatibility

The CORS implementation is compatible with the S3 `PutBucketCors`, `GetBucketCors`, and `DeleteBucketCors` API and the Azure Blob Storage service property CORS API, allowing existing tools and SDKs that manage S3/Azure CORS to work without modification.
