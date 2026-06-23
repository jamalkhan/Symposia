# Database Billing

## Overview

Database billing has two independent components that are metered separately and charged to the same tenant credit balance used for blob storage:

1. **Compute billing** — charged for CPU and RAM consumed while a database is running.
2. **Storage billing** — charged via the standard blob storage fee system for page data and WAL archives stored in Tier 1 blob buckets. No special handling; these are regular blobs.

The tenant sees one credit balance and one usage statement that itemizes both components. There is no separate billing system for databases.

---

## Compute Billing

### What Is Billed

Compute is billed on **active compute time** — time during which the Postgres process is running and consuming resources. Suspended databases (no active connections, idle past the suspend threshold) do not incur compute charges. Storage charges continue while suspended.

The billing unit is a **compute-second**: one second of a defined compute size.

### Compute Sizing

Tenants select a **compute size** at database provisioning. The compute size defines the vCPU count and RAM allocation for the Postgres process on the compute node. The available sizes mirror the instance size paradigm used by AWS RDS and Azure Database for PostgreSQL:

| Size | vCPU | RAM | Notes |
|---|---|---|---|
| `micro` | 0.5 vCPU | 1 GB | Dev/test only. Not eligible for production SLA or HIPAA. |
| `small` | 1 vCPU | 2 GB | Low-traffic apps, side projects. |
| `medium` | 2 vCPU | 4 GB | Moderate traffic, small production workloads. |
| `large` | 4 vCPU | 8 GB | Standard production. |
| `xlarge` | 8 vCPU | 16 GB | High-traffic production. |
| `2xlarge` | 16 vCPU | 32 GB | Heavy workloads, large concurrency. |
| `4xlarge` | 32 vCPU | 64 GB | Very large workloads. Requires Compute Tier 1 node. |

`micro` and `small` sizes may be placed on Compute Tier 3 nodes. `large` and above require Compute Tier 2 or above. `2xlarge` and above require Compute Tier 1.

Tenants may change their compute size at any time. The change takes effect after a brief compute restart (target: under 5 seconds). No data migration required.

### Pricing Formula

```
compute_cost_per_second = base_vcpu_rate × vCPU_count
                        + base_ram_rate  × ram_gb_count
```

Both `base_vcpu_rate` and `base_ram_rate` are governance parameters denominated in the native token, following the same model as storage pricing. Illustrative rates:

| Component | Illustrative Base Rate |
|---|---|
| vCPU | 0.00001 tokens/second (= 0.036 tokens/hour) |
| RAM (per GB) | 0.000002 tokens/second (= 0.0072 tokens/hour) |

**Tier surcharge**: Databases placed on higher compute tiers incur a tier surcharge (multiplier on the base rate), reflecting the higher quality of the underlying hardware. Exact multipliers are governance parameters.

| Compute Tier | Rate Multiplier |
|---|---|
| Compute Tier 1 | 2.0× |
| Compute Tier 2 | 1.0× (base) |
| Compute Tier 3 | 0.6× |

### Billing Granularity

Compute is billed per-second, starting when the Postgres process starts (database created or resumed from suspend) and stopping when the process is suspended or the database is deleted. There is no minimum billing increment.

### Suspend and Compute Cost

When a database is suspended (see [Postgres Architecture](./postgres-architecture.md)):
- Compute billing stops immediately at the moment of suspension.
- Storage billing continues.
- There is no charge for the suspend or resume operation itself.

The time to suspension (idle threshold) is configurable by the tenant:

| Setting | Default | Range |
|---|---|---|
| Idle suspend threshold | 15 minutes | 1 minute to "never" |

Setting "never" means the database stays active 24/7 and compute is charged continuously. This is appropriate for production databases that need consistent cold-start latency.

### Free Allowance per Epoch

Newly created tenant accounts receive a small monthly compute credit for evaluation purposes:
- 100 compute-hours at `micro` size per epoch, for the first 3 epochs (same as the free storage tier).
- After the free period, full compute rates apply.

---

## Storage Billing

All persistent data associated with a database (page blobs and WAL archives) is stored in Tier 1 blob storage buckets under the tenant's account and billed at standard Tier 1 blob storage rates (see [Data Retention and Billing](../Platform/retention-and-billing.md)).

### What Creates Storage Charges

| Source | Notes |
|---|---|
| **Database pages** | The actual data in the database (tables, indexes, sequences, system catalogs). Written by the pageserver to the tenant's Tier 1 bucket as Postgres pages. Size grows as data is inserted. |
| **WAL archives** | Streamed WAL segments uploaded to blob storage by the safekeeper, retained for backup and PITR purposes. WAL volume is proportional to write activity. High-write-rate databases accumulate WAL faster. |
| **Database backups and snapshots** | On-demand exports and snapshots (see [Backup and Restore](./backup-and-restore.md)) stored as blobs in the tenant's backup bucket. |

### What Does NOT Create Storage Charges

- The local pageserver cache on the compute node (this is the operator's hardware, not tenant storage).
- WAL held in safekeeper RAM before it is flushed to blob storage.

### Storage Pricing

Tier 1 storage pricing applies to all database storage, including page data and WAL. This is typically 3× the Tier 3 base rate (see [Data Retention and Billing](../Platform/retention-and-billing.md)). This is the correct trade-off: database page storage demands Tier 1 performance guarantees, so it is priced accordingly.

Tenants who want lower storage costs for archived backups can explicitly configure their backup snapshots to land in Tier 3 or Tier 4 buckets. Backup data does not require Tier 1 IOPS; it is cold data retrieved only on restore.

### WAL Retention and Cost Control

WAL archives accumulate over time and create ongoing storage costs. The WAL retention policy governs how long archives are retained:

| Setting | Default | Effect |
|---|---|---|
| WAL retention window | 7 days | WAL older than 7 days is garbage-collected. This limits the restore window (see Backup and Restore). |
| Minimum WAL retention | 1 day | Cannot be set lower without disabling PITR-based restore. |
| Maximum WAL retention | 90 days | Beyond this, WAL storage costs may exceed the value of the restore window for most use cases. |

Tenants can reduce WAL storage costs by:
- Shortening the WAL retention window (accepts a narrower restore window).
- Taking periodic snapshots (a snapshot "checkpoints" state, after which WAL before the snapshot is eligible for deletion even within the retention window).

---

## Usage Reporting

Database usage appears in the tenant's standard usage statement alongside blob storage, broken out by database instance:

```json
{
  "epoch": 1234,
  "databases": [
    {
      "db_id": "db_01abc",
      "name": "my-production-db",
      "compute_seconds": 86400,
      "compute_size": "large",
      "compute_tier": 2,
      "compute_cost_tokens": 12.96,
      "storage_page_bytes": 21474836480,
      "storage_wal_bytes": 5368709120,
      "storage_backup_bytes": 10737418240,
      "storage_cost_tokens": 0.89
    }
  ],
  "total_database_compute_cost_tokens": 12.96,
  "total_database_storage_cost_tokens": 0.89
}
```

API endpoint:
```
GET /databases/{db-id}/usage?epoch={n}
GET /databases/{db-id}/usage?from={date}&to={date}
```

---

## Connection Fees

Basic database connection usage has no per-connection fee. However, to prevent abuse (e.g., holding thousands of idle connections that tie up compute node resources):

- Connections held open for more than **10 minutes without any query activity** incur a small idle connection fee: 0.0001 tokens per idle connection per minute. This is deliberately small — it is a nudge to close idle connections, not a meaningful revenue source.
- The proxy layer enforces a maximum connection count per database (default: 200). Tenants who need more can request an increase, subject to compute node capacity.

---

## Open Questions (First Pass — To Iterate)

- **Read replica billing**: If a tenant runs read replicas, they are separate compute instances and billed separately. But do they share the same page storage bucket, or have their own? Shared bucket makes sense (they serve the same data), but read egress from the bucket to the replica is a storage egress charge. Need to clarify whether intra-service page fetch is billed as egress or internal.
- **Compute operator compensation**: How much of the compute billing revenue goes to the compute node operator vs. the platform? This is a governance parameter, but an initial starting point (e.g., 70% to operator, 30% platform) should be proposed.
- **Overage handling**: What happens if a tenant's database uses more compute than their credit balance covers mid-epoch? Suspend the database immediately? Allow a grace period? For HIPAA-designated databases, suspending mid-session could have compliance implications.
- **Minimum database size**: Is there a minimum storage commitment for a database, similar to a minimum blob size? Postgres system catalogs alone consume ~8 MB. This is negligible but should be documented.
