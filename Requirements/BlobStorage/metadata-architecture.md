# Blob Metadata Architecture

## Overview

Blob metadata — the index of what is stored, where, and who owns it — must be fully decentralized and must not depend on any central database. This is a hard architectural constraint driven by two facts:

1. **The Postgres database product runs on top of blob storage** (see [Postgres Architecture](../Database/postgres-architecture.md)). A blob metadata store that depended on Postgres would be circular and impossible to bootstrap.
2. **The network must remain operational when one or more nodes go offline.** Any single-node or single-service dependency in the metadata path is a network-level single point of failure.

The metadata architecture uses three layers with no central coordinator.

---

## Three-Layer Metadata Model

### Layer 1 — Per-Node Local Index (SQLite)

Each storage node maintains a local SQLite database containing its own blob manifest:

| Column | Description |
|---|---|
| `cid` | Content identifier (SHA-256 of the blob) |
| `size_bytes` | Blob size |
| `tenant_id` | Owning tenant |
| `bucket` | Bucket name |
| `key` | Object key |
| `region_tags` | Region assignments for this blob |
| `stored_at` | Timestamp when this node wrote the blob to disk |
| `checksum_verified_at` | Timestamp of last integrity check |
| `status` | `active`, `pending_gc`, `orphaned` |

This index is the node's authoritative record of what it holds. It is used for local integrity checking, GC, proof-of-possession responses, and reporting to the coordination layer. No other node depends on it.

SQLite is the correct choice here: it is embedded (no separate process), durable, ACID-compliant, and fast for single-node read/write patterns. It does not require any external database server.

### Layer 2 — Cluster Routing Table (Gossip Protocol)

The network-wide index — which nodes hold which blobs — is maintained via a gossip protocol between nodes. Each node periodically broadcasts:

- Additions: new CIDs it has written to disk
- Deletions: CIDs it has removed
- Status updates: replica counts, health changes

Gateways subscribe to the gossip stream and maintain an **in-memory routing cache** of `CID → [node_id, ...]` mappings. This cache is the fast path for read routing and write placement decisions. It is rebuilt from gossip on gateway startup and is refreshed continuously.

The gossip model is eventually consistent. A freshly written blob may not appear in every gateway's routing cache for up to 30 seconds. This is acceptable because:
- The writing gateway already knows which nodes received the blob (it just wrote to them).
- Reads from the writing gateway are immediately routable.
- Reads from other gateways within the propagation window fall back to the on-chain index.

### Layer 3 — On-Chain Merkle Roots (Authoritative)

At the end of each epoch, every node submits a signed Merkle root of its local blob manifest to the L3 chain. The chain records:

- Node public key
- Epoch number
- Merkle root of `{cid, size, tenant_id, checksum}` tuples
- Signature

These on-chain roots serve as the authoritative source of truth. They are used for:

- **Dispute resolution**: If a node claims to hold a blob but the gateway's gossip cache disagrees, the on-chain root resolves the conflict.
- **Proof of storage verification**: Verifier nodes can challenge a storage node to produce a Merkle inclusion proof for any blob in its on-chain root.
- **Reward calculation**: Epoch rewards are calculated from on-chain roots, not from self-reported metrics alone.
- **Recovery**: If a gateway's routing cache is lost, it can be rebuilt by scanning on-chain roots and replaying gossip history.

On-chain roots are not a real-time index — they lag reality by up to one epoch (typically 24 hours). They are not queried on the hot read path.

---

## Metadata Search Index (Derived Off-Chain Projection)

The metadata search index described in [Metadata Search and Object Tagging](./metadata-search-and-object-tagging.md) is a **derived projection** of the gossip routing table, not a primary data store. It is maintained by an off-chain indexing service operated by the platform.

- If the search index is lost or corrupted, it can be rebuilt from the gossip routing table and on-chain roots without data loss.
- The search index is explicitly not a dependency of the read or write path. Its unavailability degrades search queries but does not affect blob reads or writes.
- The search index may use any queryable store internally (a time-series index, an embedded document store, etc.). The implementation is an internal detail; tenants only interact with it via the search API.

---

## No Central Database Dependency

The blob storage layer has no runtime dependency on:
- Postgres (used by the database product layer above it)
- Any external relational database
- Any distributed database requiring a quorum of database nodes

The only shared state that crosses node boundaries is:
1. The gossip stream (P2P, no coordinator)
2. The L3 blockchain (decentralized, secured by Base/Ethereum)

This means the blob storage network can function correctly when any individual node — including any gateway — goes offline. The network degrades gracefully: fewer nodes means fewer replicas and reduced throughput, but the surviving nodes continue to serve data and the routing layer continues to function.

---

## Bootstrap and Cold Start

On a fresh node joining the network:

1. The node generates its keypair and registers on-chain.
2. It pulls the current set of on-chain Merkle roots from the L3 chain to understand what blobs the network expects it to eventually hold (relevant only if this node is taking over blobs from a departing node).
3. It subscribes to the gossip stream and begins receiving routing table updates.
4. Its local SQLite index starts empty. It populates as new blobs are placed on it.

On a fresh gateway joining the network:

1. It subscribes to the gossip stream and builds its in-memory routing cache.
2. During the initial cache-building window (typically under 60 seconds), it may fall back to querying peer gateways or the on-chain roots for routing decisions.
3. Once the cache is warm, it operates fully independently.

See [Network Bootstrapping and Cold Start](../Network/network-bootstrapping-and-cold-start.md) for the broader cold-start strategy.
