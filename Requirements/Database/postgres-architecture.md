# Postgres Database Architecture

## Overview

The database layer delivers managed Postgres to tenants on top of Symposia's decentralized infrastructure. The architecture separates compute from storage — the same principle that underlies the blob storage layer — and extends the network with a new participant class: **compute nodes** run by independent operators, scored and rewarded via the same epoch-based system as storage nodes.

The architecture follows the Neon open-source Postgres project's storage-separation model (Apache 2.0). Neon proves the design: stateless Postgres compute instances pull pages on demand from a page cache that is backed by object storage. Symposia's Tier 1 blob storage nodes serve as that object storage layer.

This is not a hosted database managed by the platform operator. The platform provides orchestration, routing, billing, and observability. Compute node operators provide the execution capacity. Storage node operators (already spec'd) provide the storage. The platform operator has no privileged access to tenant database contents.

---

## System Architecture

```
Tenant Application
       │
       │ (Postgres wire protocol)
       ▼
  ┌─────────────┐
  │  DB Proxy   │  ← connection pooling, routing, auth
  │  (PgBouncer │     (run by platform or proxy node operators)
  │   variant)  │
  └──────┬──────┘
         │
         ▼
  ┌─────────────┐
  │  Compute    │  ← stateless Postgres process
  │  Node       │     pulls pages from pageserver cache
  │  (Postgres) │     streams WAL to safekeeper peers
  └──────┬──────┘
         │ (pages on demand)      │ (WAL stream)
         ▼                        ▼
  ┌─────────────┐         ┌──────────────┐
  │  Pageserver │         │  Safekeeper  │  ← WAL durability ring
  │  (local     │         │  (co-located  │     (3 compute nodes act
  │   page      │         │   with        │      as mutual safekeepers)
  │   cache)    │         │   compute)    │
  └──────┬──────┘         └──────┬───────┘
         │ (cold pages)          │ (WAL archive)
         ▼                        ▼
  ┌──────────────────────────────────────┐
  │   Symposia Tier 1 Blob Storage       │
  │   (existing network, S3-compatible)  │
  └──────────────────────────────────────┘
```

---

## Components

### Compute Nodes

Compute nodes run stateless Postgres instances. "Stateless" means:
- No durable Postgres data directory on local disk. All page data lives in the pageserver cache and blob storage.
- The compute node can be restarted, replaced, or migrated to different hardware without data loss.
- Multiple compute nodes may serve the same database (read replicas, failover).

Each compute node runs:
- A Postgres process (with the Neon-compatible storage manager extension to redirect page reads to the pageserver).
- A **pageserver** process — a local in-memory and on-disk cache of recently accessed database pages. The pageserver is the hot tier; blob storage is the cold tier. Cache eviction uploads cold pages to the tenant's Tier 1 blob bucket.
- A **safekeeper** process — receiving WAL from peer compute nodes (see WAL and Durability below).

See [Compute Nodes](./compute-nodes.md) for hardware requirements, tiers, measurement, and operator incentives.

### DB Proxy Layer

Postgres clients connect to a proxy endpoint rather than directly to a compute node. The proxy provides:

- **Connection pooling**: Postgres has limited connection capacity; the proxy multiplexes many client connections to fewer backend connections.
- **Routing**: Maps each tenant database to its assigned compute node. Handles failover routing if the primary compute node becomes unavailable.
- **Authentication**: Validates the tenant's database credential before establishing a backend connection. The proxy is the authentication boundary; compute nodes accept connections only from authorized proxies.
- **Protocol compatibility**: Serves the full Postgres wire protocol (v3). Any standard Postgres client library (psycopg2, node-postgres, JDBC, etc.) connects without modification.

The proxy tier is operated by the platform. Proxy nodes are a separate class of operator that the network may open to independent operators in a future version; at launch, the platform runs the proxy tier centrally.

### WAL and Durability

Write-Ahead Log (WAL) is the most latency-sensitive component of the stack. A Postgres commit is not acknowledged to the client until WAL has been durably recorded. This means WAL cannot go through the standard blob storage write path (fan-out + quorum + network hops adds too much latency).

**Safekeeper model**: Each compute node also runs safekeeper software. When Postgres on Compute Node A commits a transaction, it streams WAL to the safekeepers running on two peer compute nodes (B and C) in the same region. All three must acknowledge before the commit is confirmed to the client. This is a 3-node quorum restricted to low-latency peers.

Requirements for safekeeper participation:
- Round-trip latency between the compute node and its two safekeeper peers must be ≤ 5ms (measured continuously).
- Safekeepers must be in the same region as the primary compute node.
- Safekeepers must have fast local durable storage (NVMe SSD) for WAL journaling — WAL is not stored in blob storage in the hot path.

After a WAL segment is confirmed by the safekeeper quorum, the segment is asynchronously uploaded to the tenant's Tier 1 blob storage bucket for long-term durability. This uploaded WAL is the source of truth for backup and point-in-time recovery (see [Backup and Restore](./backup-and-restore.md)).

### Page Storage in Blob Storage

The pageserver writes cold pages to the tenant's designated Tier 1 blob bucket using the same S3-compatible interface as any other blob write. From the storage network's perspective, database pages are encrypted blobs — the storage nodes have no knowledge that the bytes are database pages.

Requirements for the blob storage tier used by databases:
- Must be Tier 1 (≥50K IOPS, ≤5ms TTFB). Placement of database page blobs on lower-tier nodes is not permitted.
- The database page bucket must be in the same region as the compute node to keep pageserver → blob storage latency within budget.
- Writes to this bucket are billable as standard blob storage (see [Database Billing](./database-billing.md)).
- The page bucket is tenant-controlled (same credentials system) but the pageserver writes to it directly using a narrow-scoped credential issued at database provisioning time. The tenant does not need to manage this bucket manually.

---

## Database Lifecycle

### Provisioning

When a tenant provisions a new Postgres database:

1. The platform selects an available compute node in the tenant's chosen region with sufficient capacity.
2. A Tier 1 blob bucket is provisioned for page storage and WAL archival.
3. Two peer compute nodes in the same region are assigned as safekeepers for this database.
4. The proxy layer is updated with the routing entry for the new database.
5. The tenant receives a Postgres connection string: `postgres://[db-id].[region].db.symposia.network:5432/[dbname]`
6. The first connection initializes the Postgres cluster (equivalent to `initdb`); subsequent connections find it ready.

Target provisioning time from API call to ready connection: **under 30 seconds**.

### Scaling

- **Compute scaling**: A tenant may change their database's compute tier (vCPU and RAM allocation) at any time. This requires a brief restart of the Postgres process (target: under 5 seconds), not a data migration. Pages in blob storage are untouched.
- **Storage scaling**: Storage scales automatically as the database grows. No pre-provisioning of storage capacity is required; tenants pay for what they use.
- **Read replicas**: A tenant may provision additional compute nodes pointing to the same blob storage bucket. Read replicas receive WAL from the primary and serve read-only queries. They are billed as separate compute instances.

### Suspend and Resume

When a database has had no active connections for a configurable idle period (default: 15 minutes; minimum: 1 minute; disable: available), the compute process is suspended:
- The Postgres process is gracefully shut down.
- The pageserver flushes remaining in-memory pages to blob storage.
- No compute fees accrue while suspended.
- Storage fees (per-byte-per-epoch) continue.

On the next incoming connection, the proxy wakes the compute node (cold start target: under 3 seconds for databases with a warm pageserver cache on the same node). The tenant's application may need a brief retry on first connection after a suspend period.

### Deletion

When a tenant deletes a database:
- All page data in the blob bucket is soft-deleted (follows standard retention and billing schedule).
- WAL archives in the bucket are soft-deleted.
- The compute node assignment and proxy routing entry are removed.
- After the soft-delete recovery window, data is permanently removed from all nodes.

---

## Tenant Connection Model

Each database instance has:
- A **primary connection string** for read-write workloads: routed to the primary compute node.
- An optional **read-only connection string** for read replicas.
- **Database credentials** are Postgres-native (username/password), distinct from the platform API credentials. Database credentials are managed via the API:

```
POST   /databases/{db-id}/users         Create a database user
DELETE /databases/{db-id}/users/{user}  Drop a database user
PUT    /databases/{db-id}/users/{user}  Rotate password
GET    /databases/{db-id}/users         List users
```

Tenant application code connects with standard Postgres client libraries. No platform-specific client library is required for basic database access.

---

## Encryption

Database pages stored in blob storage are encrypted identically to any other tenant blob — tenant-managed KMS keys, ciphertext only ever stored on nodes (see [Security](../Platform/security.md)). The compute node process holds the decrypted pages in memory during query execution. The pageserver cache holds decrypted pages in memory and encrypted pages on local disk/in blob storage.

This means:
- A compute node operator sees encrypted blobs on disk (in the pageserver's local cache) and decrypted pages in RAM during active query execution.
- A storage node operator sees only encrypted blobs — identical to any other stored object.
- The platform has no access to decrypted database contents.

HIPAA-designated databases follow the same controls as HIPAA-designated blob buckets: BAA required from compute node operators as well as storage node operators, since compute operators hold decrypted pages in RAM during active queries.

---

## Open Questions (First Pass — To Iterate)

- **Compute node operator BAA requirement**: Should all compute node operators be required to sign a BAA, or only those that opt into HIPAA-designated workloads? Given that compute nodes hold decrypted pages in RAM, the argument for universal BAA is strong. This has implications for operator onboarding friction.
- **Connection proxy operator model**: At launch, the platform runs the proxy tier centrally. What's the path to decentralizing it? Proxy operators would need to handle tenant authentication and routing, which creates a privileged role.
- **Multi-region databases**: Currently scoped to single-region compute with Tier 1 blob storage in the same region. Cross-region replicas are in scope for a future version but introduce distributed transaction complexity.
- **Postgres version policy**: Which Postgres major version(s) are supported? How are major version upgrades handled? This needs its own requirements once the architecture is stable.
- **Extensions**: Which Postgres extensions are available on compute nodes? PostGIS, pgvector, pg_cron, etc. each require extension software to be installed on compute nodes. Node operators need to know what they're expected to provide.
