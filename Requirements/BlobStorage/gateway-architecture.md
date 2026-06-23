# Gateway Architecture

## Overview

The gateway is the entry point for all tenant traffic. It is a stateless HTTP server that sits between clients and storage nodes. It handles authentication, authorization, request routing, write orchestration, read routing, and bandwidth budgeting. It does not store blob data — it is a pure coordination and routing layer.

Because clients manage their own encryption (see [Security](../Platform/security.md)), the gateway handles only ciphertext. It never sees plaintext tenant data and holds no encryption keys. This simplifies the gateway significantly: it is an intelligent proxy, not a cryptographic participant.

---

## Gateway Roles and Responsibilities

| Responsibility | Detail |
|---|---|
| **Authentication** | Validates credentials on every request before any other processing. |
| **Authorization** | Enforces credential scope (read-only, bucket-scoped, etc.). |
| **Write orchestration** | Selects target nodes per placement rules, fans out writes, enforces write quorum. |
| **Read routing** | Selects the best available node for a read and proxies the response. |
| **Multipart coordination** | Tracks in-progress multipart uploads and assembles part manifests. |
| **Metadata operations** | Handles LIST, HEAD, tag, and metadata queries via the metadata index. |
| **CORS enforcement** | Applies bucket-level CORS rules to all responses. |
| **Rate limiting** | Enforces per-credential and per-IP rate limits. |
| **Bandwidth budgeting** | Throttles background replication traffic to protect client-facing bandwidth. |
| **Health and readiness** | Exposes health check endpoints for load balancers. |

The gateway does **not**:
- Store blob data locally (no disk required beyond OS and application).
- Hold tenant encryption keys or decrypt data.
- Participate in region verification or epoch reward calculations.
- Run blockchain node software.

---

## Gateway Instances and Discovery

### Multiple Gateways

Multiple gateway instances run in parallel for redundancy and throughput. They are stateless — any gateway can handle any request. There is no sticky routing requirement.

Gateway instances are deployed:
- By the platform operator (always — at least two instances for redundancy).
- Optionally by node operators who want to offer gateway services to their local region (announced via the node registry).

### Client Discovery

Clients discover gateway endpoints via:

1. **Well-known DNS endpoint**: A platform-operated DNS name (e.g., `gateway.network.example`) resolves to multiple gateway IPs via DNS round-robin or anycast. This is the default for all SDK and CLI usage.
2. **Regional DNS endpoints**: Per-region gateway endpoints (e.g., `us-east.gateway.network.example`) for clients who want to pin to a specific region for lower latency.
3. **Direct endpoint configuration**: Clients may configure a specific gateway URL (useful for private deployments or testing with local dev mode).

All gateway endpoints are HTTPS only. Plain HTTP is rejected.

---

## Write Path

When a client sends a PUT or multipart upload:

1. **Authentication and authorization** are validated. Rejected immediately on failure.
2. **Quota check**: The tenant's current usage is checked against any configured quota. Rejected with `507 Insufficient Storage` if exceeded.
3. **Placement decision**: The gateway consults the placement engine to select target nodes, respecting region assignment, performance tier, fault domain rules, and current node health. The number of target nodes equals the blob's required copy count (see [Redundancy and Data Integrity](./redundancy-and-data-integrity.md)).
4. **Fan-out**: The gateway opens parallel connections to all target nodes and streams the encrypted blob to each simultaneously. The gateway does not buffer the full blob in memory before forwarding — it pipes the incoming request stream to all nodes concurrently (streaming fan-out).
5. **Write quorum**: The gateway waits for the minimum quorum of nodes to confirm receipt (see [Write Quorum and Consistency](./write-quorum-and-consistency.md)). Once quorum is reached, it returns HTTP 201 to the client. Remaining replicas complete asynchronously.
6. **Metadata commit**: After quorum confirmation, the gateway submits a metadata record (key, size, content hash, node list, region assignments, tier, timestamp) to the coordination layer. The blob is not visible in LIST results until the metadata commit is acknowledged.
7. **ETag response**: The gateway returns the blob's content hash as the ETag in the response.

### Write Failure Handling

- If fewer than quorum nodes confirm within the write timeout (default: 30 seconds), the write fails with `503 Service Unavailable`. The gateway instructs all nodes that did receive data to discard it (or marks it orphaned for GC).
- If the client disconnects mid-upload, the gateway cancels the fan-out and marks any partial uploads for GC.
- The gateway retries individual node connections once on transient network errors before failing quorum.

---

## Read Path

When a client sends a GET:

1. **Authentication and authorization** validated.
2. **Metadata lookup**: The gateway queries the metadata index for the blob's replica list, tier, and health status.
3. **Node selection**: The gateway selects the best available node using the scoring algorithm (see [Node Selection for Reads](./node-selection-for-reads.md)).
4. **Proxy**: The gateway opens a connection to the selected node, fetches the encrypted blob, and streams it to the client. The gateway does not buffer the full response.
5. **Integrity check**: As data streams through, the gateway computes a running hash and compares it to the stored ETag on completion. A mismatch triggers a read repair (discard the response to the client, return `503`, and flag that replica for repair).
6. **Range requests**: If the client sent a `Range` header, the gateway forwards the range to the node and proxies the partial response.

### Read Failure Fallback

- If the selected node fails mid-stream or returns an error, the gateway immediately retries from the next-best available node. The client receives a seamless response with no visible error if the retry succeeds within the timeout.
- If all replicas fail or are unavailable, the gateway returns `503 Service Unavailable` with a `Retry-After` header.

---

## Gateway-Node Protocol

Communication between the gateway and storage nodes uses a private binary protocol over TLS (not the public S3/Azure HTTP interface). The protocol supports:

- Blob write (streaming, with acknowledgement after durable write).
- Blob read (streaming, with byte range support).
- Blob delete.
- Health probe (is this node available and what is its current load?).
- Integrity challenge (request a proof-of-possession for a specific blob).

The protocol is versioned. Gateways and nodes negotiate the protocol version on connection establishment. Older nodes running an older protocol version interoperate with newer gateways via a compatibility shim.

---

## Node Health Cache

The gateway maintains a local in-memory cache of node health and performance data, refreshed from:

- P2P gossip from the node network (continuous, sub-minute updates).
- Direct health probes to nodes in the gateway's routing table (every 30 seconds).
- On-chain node registry and tier data (refreshed each epoch).

The cache is used for placement decisions and node selection. A node that fails a direct health probe is immediately marked degraded in the cache and excluded from new writes until it recovers.

---

## Scaling and Redundancy

- Gateways are horizontally scalable. Adding more gateway instances increases write and read throughput linearly.
- A load balancer in front of the gateway tier handles traffic distribution. The platform operates this load balancer; node-operator-run gateways manage their own.
- Gateways have no shared state — all state lives in the node network, metadata index, and blockchain. A gateway that crashes loses nothing; the next request is served by another instance.
- Gateway instances in multiple geographic regions reduce latency for regional tenants and provide redundancy against regional outages.

---

## Operational Requirements

- Gateways must be reachable from the public internet (they are the only component that is — storage nodes do not need inbound public connectivity).
- Minimum deployment: 2 gateway instances in at least 2 distinct fault domains (separate data centers or cloud availability zones).
- Gateway processes must support graceful shutdown: in-flight requests are completed before the process exits; new connections are rejected during the drain window.
- Gateway logs emit structured JSON for all requests: timestamp, tenant ID, credential ID, operation, bucket, key, response code, bytes transferred, latency, selected node ID, and any error detail.
