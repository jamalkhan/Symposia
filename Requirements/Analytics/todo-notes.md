# Analytics Database — Notes (Placeholder)

**Status**: Not yet in scope. This document captures the intent and likely technology direction for a future analytics database layer. It will be replaced with full requirements when this capability is prioritized.

---

## Intent

Once the OLTP database layer (Postgres via Neon architecture) is live, the next database layer will target **analytical workloads (OLAP)**: large scans, aggregations, columnar reads, and data warehousing use cases. These workloads have fundamentally different characteristics from OLTP and should not be forced through a row-oriented Postgres storage engine.

## Likely Technology Direction

### DuckDB

DuckDB is an in-process analytical database engine — it runs embedded within a client process, requires no separate server, and reads directly from files in object storage (Parquet, CSV, JSON, Arrow IPC). It is MIT-licensed, actively developed, and has become the dominant choice for local analytics and embedded OLAP.

**Why it fits here**: Symposia's S3-compatible interface means DuckDB can query Parquet files stored in blob storage directly using DuckDB's native S3 extension, with no additional infrastructure. A tenant can store Parquet files in their blob buckets and query them with DuckDB without provisioning any database compute. This is a zero-marginal-infrastructure use case for the platform — existing Tier 3/4 nodes serve the scans.

The compute requirement is on the client side, not the platform side. This is a meaningful simplification relative to the OLTP stack.

**Open questions for when this is scoped**:
- Do we offer a hosted DuckDB query API (submit SQL, get results), or do we just ensure S3 compatibility is sufficient for clients to run their own DuckDB?
- A hosted DuckDB API would need compute node participation. Does this reuse the OLTP compute node operator network, or is it a separate node class?
- How does billing work? Per-query (bytes scanned), per-compute-time, or per-result-row?

### ClickHouse

ClickHouse is a standalone columnar database optimized for high-throughput analytical queries over large datasets — billions of rows, sub-second responses. It is Apache 2.0 licensed and widely deployed.

**Why it fits here**: ClickHouse supports object storage (S3/GCS/Azure) as a primary storage backend via its `MergeTree` engine variants. A compute node running ClickHouse could use Symposia blob storage as its data layer, with the same Tier 1/Tier 2 nodes serving hot data.

ClickHouse requires persistent compute (it is not serverless/embedded like DuckDB) and would require a new compute node profile: high-memory, high-CPU, less emphasis on IOPS than the OLTP compute tier.

**Open questions**:
- ClickHouse replication has its own keeper (Keeper, formerly ZooKeeper). How does this interact with Symposia's coordination layer?
- For a decentralized deployment, who runs the ClickHouse compute? Same compute node operator model as OLTP?
- ClickHouse is suited to one specific write pattern (bulk inserts via the MergeTree). Does this clash with tenants who want real-time streaming inserts?

## Recommended Decision Point

When scoping this, the first decision is:

1. **DuckDB-as-a-service** (hosted query execution against Parquet files in blob storage) — simpler to implement, lower compute requirements, fits well with data engineering workflows.
2. **ClickHouse-as-a-service** (persistent columnar store with its own compute nodes) — more powerful, more infrastructure, more operator complexity.

These are not mutually exclusive. DuckDB first (lower-hanging fruit), ClickHouse later as a higher-tier offering.
