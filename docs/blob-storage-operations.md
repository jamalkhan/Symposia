# Blob storage operations

This page is for operators running Symposia blob gateways and storage nodes. It covers health checks, metrics, write durability settings and requests from enterprise tenants.

## Health, readiness and metrics

Both the gateway and the storage node serve these endpoints (BlobStorage #1, `Requirements/BlobStorage/health-check-endpoints.md`):

| Endpoint | Auth | Use it for |
|---|---|---|
| `GET /health` | none | Liveness. Restart the process if this doesn't return `200` within 100 ms. |
| `GET /ready` | none | Readiness. Take the instance out of rotation on `503`. The body's `reason` says why. |
| `GET /metrics` | `Authorization: Bearer <token>` | Prometheus scrape. |

`/healthz/live` and `/healthz/ready` still answer for one release. Move load balancer checks to `/health` and `/ready`.

Set the scrape token with `Gateway:MetricsBearerToken` or `StorageNode:MetricsBearerToken`. While it is empty, every scrape gets `401`.

Gateway not-ready reasons: `draining`, `node_cache_not_loaded`, `metadata_index_not_connected`, `insufficient_eligible_nodes`.
Storage node not-ready reasons: `draining`, `network_not_joined`, `registration_not_confirmed`, `region_verification_pending`, `storage_not_writable`.

### Rolling deploys

When a process starts shutting down, `/ready` turns `503 draining` at once. `DrainNoticeSeconds` (default 0) holds the listeners open that long, so the load balancer notices first. Set it to at least your health-check interval times the unhealthy threshold. In-flight requests then get `ShutdownTimeoutSeconds` (default 30) to finish.

### Request logs

The gateway writes one line per request, under the `Symposia.BlobStorage.Gateway.RequestLog` category. Each line has the tenant, credential, operation, bucket, key, status, bytes in and out, latency, the node that served the request and any error. The shipped `appsettings.json` uses the JSON console formatter, so each line is one JSON object. Health probes log at `Debug`.

## Redundancy mode

`Gateway:Redundancy:Mode` chooses how writes are placed (BlobStorage #7, #8):

- **`Bootstrap`** (default): every writable node gets a copy, `Gateway:WriteQuorumCount` acks make a quorum, and `Gateway:MinCopiesPerObject` is the floor that repair keeps. Regions are not enforced. Use it for single-node development and for networks that don't span regions yet.
- **`Network`**: the full tables from `write-quorum-and-consistency.md` and `redundancy-and-data-integrity.md`. A global object gets 4 copies with a quorum of 3. One region gets 5 copies, quorum 3, at least 2 acks in the region. Two regions get 6, quorum 4. Three regions get 7, quorum 4. N ≥ 4 regions get 2N, quorum N+1. A write is acknowledged at quorum, and the remaining copies land in the background.

Give each node a region with `StorageNode:Region` on the node. To set one for a node from the gateway, use `Gateway:Redundancy:NodeRegions`, a list of `{ "Node": "<url or node id>", "Region": "us-east" }` entries. The region the node reports wins.

## Write quorum timeout and enterprise requests

A write fails with `503` when its quorum doesn't confirm within `Gateway:Redundancy:WriteQuorumTimeoutSeconds` (default 30). The clock starts once the request body has been sent, so a slow upload isn't cut short. Copies that landed anyway are queued for garbage collection.

Enterprise tenants with very large objects or slow networks can ask for a longer timeout. To grant one:

1. Confirm the tenant ID and the timeout they need.
2. Add it to `Gateway:Redundancy:TenantWriteQuorumTimeoutSeconds` on every gateway, for example `"TenantWriteQuorumTimeoutSeconds": { "acme-corp": 120 }`.
3. Roll the gateways.

The override applies only to that tenant's writes.
