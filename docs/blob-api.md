# Symposia blob API (`/v1`)

The blob gateway serves two APIs over the same buckets and objects:

- the **S3-compatible API**, at `/{bucket}` and `/{bucket}/{key}`, for existing S3 tools and SDKs;
- the **Symposia-native API**, under `/v1`, for things S3 doesn't model (#203).

An object written through either API reads back through the other. The machine-readable contract is at `GET /v1/openapi.json`, which needs no authentication.

## Authentication

Sign every request except `/v1/openapi.json` with **AWS Signature V4** using the gateway's access keys. Use service `s3` and the gateway's region, which defaults to `us-east-1`. Any SigV4 signer works, for example:

```sh
curl --aws-sigv4 "aws:amz:us-east-1:s3" --user "$ACCESS_KEY:$SECRET_KEY" \
  -H "x-amz-content-sha256: UNSIGNED-PAYLOAD" https://gateway.example/v1/buckets
```

A missing or bad signature gets `401` and a JSON error. The S3 surface keeps S3's `403` XML.

## Endpoints

| Method and path | What it does |
|---|---|
| `GET /v1/buckets` | List buckets. |
| `PUT /v1/buckets/{bucket}` | Create a bucket. Idempotent. S3 naming rules apply. |
| `DELETE /v1/buckets/{bucket}` | Delete an empty bucket. `409 BucketNotEmpty` otherwise. |
| `GET /v1/buckets/{bucket}/objects?prefix=&cursor=&limit=` | List by key. `limit` 1–1000, default 100. Pass `nextCursor` back as `cursor`. |
| `PUT /v1/buckets/{bucket}/objects/{key}` | Write the raw request body. Returns the object as JSON. |
| `GET /v1/buckets/{bucket}/objects/{key}` | Read. Supports `Range`. |
| `GET /v1/buckets/{bucket}/objects/{key}?meta` | Metadata, including the content ID (CID) and replica placement. |
| `HEAD /v1/buckets/{bucket}/objects/{key}` | Headers only. |
| `DELETE /v1/buckets/{bucket}/objects/{key}` | Delete. Idempotent. |

Keys may contain `/`. Capabilities attach to the object path as query flags (`?meta`, and `?history` from #204), so a key ending in a word like `history` is never ambiguous.

## Errors and request IDs

Errors look like `{"error":{"code":"NoSuchKey","message":"…","requestId":"…"}}`. Codes and HTTP statuses mirror S3's.

Every response, on either API, carries `x-symposia-request-id`. S3 responses also carry it as `x-amz-request-id`.
