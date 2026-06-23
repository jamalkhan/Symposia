# Backup and Restore

## Overview

Two distinct backup capabilities are provided:

1. **Snapshot and restore** — take a point-in-time snapshot of a database that can be restored to a new database instance. Implemented via WAL-based point-in-time recovery (PITR) under the hood, but exposed to tenants as a simple "take snapshot / restore to snapshot" interface. No branching, no history browsing — just a clean restore target.

2. **Export and download** — generate a portable database dump (pg_dump format) and store it as a downloadable blob. The tenant owns the export file and can restore it anywhere that runs Postgres, not just on Symposia.

Both capabilities use the tenant's blob storage as the storage medium. Backup data is stored in a tenant-controlled bucket. Tenants own their backups.

---

## How It Works Under the Hood

### WAL-Based PITR (Snapshots)

Every running database continuously uploads WAL segments to the tenant's Tier 1 blob bucket (see [Postgres Architecture](./postgres-architecture.md)). These WAL segments, combined with a base page snapshot, allow the system to reconstruct the exact state of the database at any point in time within the WAL retention window.

A "snapshot" in the tenant-facing API is a **named restore point**: a marker recording the WAL Log Sequence Number (LSN) at the moment the snapshot is taken. To restore to a snapshot, the system:

1. Spins up a new compute node.
2. Fetches the base page data from blob storage.
3. Replays WAL from the base through to the snapshot LSN.
4. Starts Postgres at the resulting state.
5. Returns a new database connection string.

The original database is unchanged. Restore creates a new database instance, not an in-place overwrite. The tenant may then:
- Use the restored database as the new primary (and delete the old one).
- Query the restored database to recover specific data, then delete it.
- Keep both running simultaneously (each billed as a separate compute instance).

### Export (pg_dump)

An export is a logical dump of the database in Postgres-native format. The system:

1. Initiates a `pg_dump` job on the source compute node using a read-only snapshot (no locking on the live database).
2. Streams the dump output to a new blob in the tenant's backup bucket (compressed, `.dump` format or plain SQL, tenant's choice).
3. Notifies the tenant when the export is complete with a download URL (presigned blob URL, see [Presigned URLs](../BlobStorage/presigned-urls.md)).

The export file is a standard Postgres dump. Any `pg_restore` or `psql` instance running compatible Postgres version can restore it.

---

## Snapshots

### Taking a Snapshot

```
POST /databases/{db-id}/snapshots

{
  "name": "before-schema-migration",
  "description": "Snapshot before v2.4.0 release migration"
}
```

Response:
```json
{
  "snapshot_id": "snap_01abc",
  "db_id": "db_01abc",
  "name": "before-schema-migration",
  "created_at": "2026-06-23T14:00:00Z",
  "lsn": "0/3A1B2C3",
  "status": "complete",
  "size_bytes_estimate": 0
}
```

Snapshots are **near-instantaneous** — taking a snapshot records the current LSN and creates a metadata entry. No data is copied at snapshot time. The cost of a snapshot is the cost of retaining WAL back to the snapshot's LSN, which extends the effective WAL retention window for that snapshot.

### Snapshot Retention

Snapshots are retained until explicitly deleted. While a snapshot exists:
- WAL segments back to the snapshot's LSN are retained (even if older than the WAL retention window default).
- The tenant is billed for WAL storage back to the oldest snapshot's LSN.

Tenants should delete snapshots they no longer need to control WAL storage costs.

```
DELETE /databases/{db-id}/snapshots/{snapshot-id}
```

### Listing Snapshots

```
GET /databases/{db-id}/snapshots
```

Returns all snapshots in reverse chronological order with their LSN, creation time, estimated WAL storage consumed since that LSN, and status.

---

## Restore from Snapshot

```
POST /databases/{db-id}/snapshots/{snapshot-id}/restore

{
  "new_db_name": "my-production-db-restored",
  "compute_size": "large",
  "region": "us-east"
}
```

Creates a new database instance from the snapshot. The restored database:
- Is an independent instance — changes to it do not affect the source database.
- Uses the same blob storage Tier 1 bucket (pages are shared, not copied, until the restored instance diverges via new writes — copy-on-write at the page level handled by the pageserver).
- Is billed as a new compute instance from the moment it starts.
- Receives its own connection string.

Target restore time from API call to ready connection: **under 5 minutes** for databases up to 1 TB (most of the time is WAL replay; larger databases or older snapshots take longer).

Restore status is queryable:

```
GET /databases/{db-id}/snapshots/{snapshot-id}/restore/{restore-id}
```

---

## Export and Download

### Initiating an Export

```
POST /databases/{db-id}/exports

{
  "format": "custom",        // "custom" (pg_dump -Fc) or "plain" (SQL text)
  "compress": true,
  "destination_bucket": null // null = platform-managed backup bucket; or specify tenant bucket
}
```

| Format | Notes |
|---|---|
| `custom` | Binary pg_dump format. Supports selective restore (specific tables, schemas). Smaller file size. Requires `pg_restore` to restore. |
| `plain` | Plain SQL text. Human-readable. Can be piped directly to `psql`. Larger file size. |

The export runs as a background job. The database continues to serve live traffic normally; the export reads from a consistent snapshot taken at the start of the export job.

Response (accepted, not complete):
```json
{
  "export_id": "exp_01abc",
  "db_id": "db_01abc",
  "status": "running",
  "format": "custom",
  "started_at": "2026-06-23T14:00:00Z",
  "estimated_completion": "2026-06-23T14:08:00Z"
}
```

### Checking Export Status

```
GET /databases/{db-id}/exports/{export-id}
```

When complete:
```json
{
  "export_id": "exp_01abc",
  "status": "complete",
  "completed_at": "2026-06-23T14:07:32Z",
  "size_bytes": 2147483648,
  "blob_key": "backups/db_01abc/exp_01abc.dump",
  "download_url": "https://gateway.example/backup-bucket/backups/...",
  "download_expires_at": "2026-06-24T14:07:32Z"
}
```

The `download_url` is a presigned URL valid for 24 hours. A fresh download URL can be generated at any time:

```
POST /databases/{db-id}/exports/{export-id}/download-url
```

### Export Billing

Exports are billed as:
- Blob storage for the exported file (at the destination bucket's tier rate — typically Tier 3 or 4, since export files are cold data).
- Compute time on the source compute node during the export job.
- Blob egress when the file is downloaded.

---

## Restore from Export

Restoring from an export file is a standard Postgres restore operation. The tenant downloads the export file and runs:

```bash
# For custom format:
pg_restore -h [connection-string] -d [dbname] export.dump

# For plain SQL:
psql -h [connection-string] -d [dbname] < export.sql
```

To restore to a new database on Symposia from an export:
1. Provision a new empty database via the API.
2. Download the export file.
3. Run `pg_restore` or `psql` against the new database's connection string.

This is a standard Postgres workflow. No Symposia-specific tooling is required. The platform will provide a CLI helper command (`blob db restore-from-export`) that automates steps 2 and 3.

---

## Automated Backup Policy

Tenants may configure an automated backup schedule to ensure regular snapshots are taken without manual intervention:

```json
{
  "auto_snapshot": {
    "enabled": true,
    "schedule": "0 2 * * *",       // cron expression — daily at 2 AM UTC
    "retention_count": 7            // keep the last 7 snapshots; delete older ones
  },
  "auto_export": {
    "enabled": false
  }
}
```

When `retention_count` is exceeded, the oldest snapshot is automatically deleted. This controls WAL storage costs: with a 7-day daily snapshot schedule and a 7-snapshot retention, WAL only needs to be retained back 7 days.

HIPAA-designated databases must have auto-snapshot enabled with a minimum retention of 30 days, as part of the HIPAA data backup requirements (§164.312(c)(1)).

---

## Backup Storage Location

By default, snapshots (WAL archives and page data) are stored in the same Tier 1 bucket as the live database's page data. Export files are stored in a separate tenant-controlled bucket (default: a platform-provisioned `backup` bucket at Tier 3).

Tenants may configure a different destination bucket for exports:
- Export files should not be in Tier 1 buckets (they don't need IOPS; Tier 3 or 4 is appropriate and cheaper).
- Snapshots cannot be moved to lower-tier storage because WAL replay requires the base page data to be accessible at Tier 1 IOPS during restore.

---

## Recovery Time and Recovery Point Objectives

| Scenario | Recovery Time Objective | Recovery Point Objective |
|---|---|---|
| Restore from snapshot (WAL-based) | < 5 minutes for databases ≤ 1 TB | Exact moment snapshot was taken (zero data loss from snapshot point) |
| Restore from export | 10–60 minutes depending on database size | Time of export (data written after export is not included) |
| Compute node failure (no data loss) | < 30 seconds (proxy reroutes to new compute node; page data in blob storage is intact) | Zero (no data loss; WAL safekeepers preserved committed transactions) |

---

## Constraints and Limitations

- **Snapshot restores create new database instances** — there is no in-place restore that overwrites the running database. Tenants must explicitly delete the old database if they want to replace it with the restore.
- **WAL-based restore requires WAL continuity** — if WAL for a period is missing (e.g., the WAL retention window expired before a snapshot was deleted), restore to a snapshot older than the window is not possible. The system warns tenants when snapshots are at risk of losing their WAL continuity.
- **Export format compatibility** — exports are compatible with the same Postgres major version used on the source database. Restoring a `pg_dump` from Postgres 16 to a Postgres 15 instance may require `--no-acl` and schema adjustments. The export metadata records the Postgres version.
- **Maximum export size** — there is no hard limit on export size, but very large databases (> 1 TB) may take several hours to export. Tenants should plan accordingly and not use exports as the primary recovery mechanism for very large databases (prefer snapshot-based restore).
