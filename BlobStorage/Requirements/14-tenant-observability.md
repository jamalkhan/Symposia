# Tenant Observability

## Overview

Tenants need real-time visibility into their own data — not just for billing awareness, but to understand how their data is being stored, where it is, how healthy it is, and how it is being accessed. Observability is a first-class feature, not an afterthought. A tenant should never have to contact support to understand the state of their own storage.

All observability data is scoped strictly to the requesting tenant. No tenant can see any data belonging to another tenant.

---

## Usage Dashboard

The usage dashboard provides a live view of the tenant's consumption and cost. It is available via the web console and as a queryable API.

### Metrics Displayed

| Metric | Description |
|---|---|
| **Storage used** | Current total bytes stored across all buckets, broken down by bucket and prefix. |
| **Storage by tier** | Bytes stored at each performance tier (Tier 1–4). |
| **Storage by region** | Bytes stored per region, showing the footprint of replica placement. |
| **Egress this epoch** | GB transferred to clients during the current epoch. |
| **Egress this month** | Rolling 30-day egress total. |
| **Request counts** | PUT, GET, DELETE, LIST operations during the current epoch. |
| **Current epoch cost** | Estimated token cost for the current epoch at the current burn rate. |
| **Credit balance** | Remaining prepaid credit in tokens and estimated days remaining at current burn rate. |
| **Epoch burn rate** | Average tokens consumed per epoch over the trailing 7 epochs. |

### Historical Views

- Usage history queryable by epoch, day, week, or custom date range.
- Per-bucket and per-prefix breakdown for any historical period.
- Downloadable as CSV or JSON for external analysis.

---

## Blob and Replication Health

Tenants can inspect the health and placement of any blob they own without downloading it.

### Per-Blob Health View

Available via `HEAD <bucket>/<key>` (extended response headers) or a dedicated metadata endpoint:

- **Replica count**: How many healthy copies currently exist.
- **Target replica count**: What the system is aiming for based on region assignment.
- **Replica locations**: Which regions each copy resides in (node identities are not disclosed to tenants — only regions).
- **Replica health per copy**: Online / Degraded / Offline status for each replica.
- **Last integrity check**: When the most recent proof-of-possession check passed for each replica.
- **Performance tier**: Which tier the primary serving replica is on.
- **Pending operations**: Whether a re-replication, repair, or tier migration is in progress for this blob.

### Bucket-Level Health Summary

- Total blobs in bucket.
- Count of blobs at or above target replica count.
- Count of blobs currently being repaired.
- Count of blobs with at least one degraded or offline replica.
- Count of blobs below minimum replica count (should always be zero in a healthy network; non-zero warrants investigation).

### Network-Level Health (Read-Only, Public)

A public status endpoint exposes aggregate network health metrics with no tenant-specific data:

- Current active node count by region and tier.
- Network-wide average uptime, latency, and throughput.
- Count of blobs currently undergoing repair.
- Current epoch number and time until next epoch seal.
- Any active incidents affecting availability.

---

## Access Logs

Tenants have full access to their own access logs in a queryable format. This is distinct from the compliance-oriented audit log — the access log is optimized for operational analysis.

### Log Fields Per Event

- Timestamp (ISO 8601, UTC)
- Operation (PUT, GET, DELETE, LIST, HEAD, COPY)
- Bucket and key
- Credential ID used
- Source IP address
- HTTP status code returned
- Bytes transferred
- Time to first byte (ms)
- Total request duration (ms)
- Region and tier of the serving node
- Whether the request was served from cache

### Query Capabilities

- Filter by bucket, key prefix, credential, operation type, status code, time range.
- Sort by any field.
- Aggregate: total requests, total bytes, average TTFB, error rate — over any time range.
- Export as CSV or JSON.
- Real-time tail: stream new log entries as they are generated (useful for debugging active integrations).

### Retention

Access logs follow the same retention rules as audit logs: 1 year standard, 6 years for ePHI-designated buckets.

---

## Alerts and Notifications

Tenants configure alert rules that trigger notifications via webhook, email, or both.

### Built-In Alert Conditions

| Alert | Default Threshold | Configurable |
|---|---|---|
| Credit balance low | 30 days remaining | Yes |
| Credit balance critical | 7 days remaining | Yes |
| Credit balance zero | 0 days remaining | Yes (cannot disable) |
| Blob below minimum replica count | Any occurrence | Yes |
| Blob offline (all replicas) | Any occurrence | Yes |
| Egress spike | 2× 7-epoch average in one epoch | Yes |
| Storage growth spike | 2× 7-epoch average in one epoch | Yes |
| Request error rate high | >1% error rate in one epoch | Yes |
| Repair in progress | Any blob entering repair | Yes |

### Custom Alerts

Tenants may define custom alerts on any metric with configurable thresholds and cooldown periods to prevent alert fatigue.

### Notification Channels

- **Webhook**: POST to a tenant-configured URL with a signed JSON payload. The signature uses an HMAC derived from a tenant-held secret, allowing the receiver to verify authenticity.
- **Email**: Sent to one or more tenant-configured addresses.
- Multiple channels may be configured for the same alert.

---

## Performance Insights

Beyond raw metrics, the observability layer surfaces actionable insights:

- **Tier recommendation**: If a bucket's access pattern shows it is consistently cold but placed on Tier 2 nodes, the system surfaces a recommendation to downgrade to Tier 3 to reduce cost.
- **Region recommendation**: If most access to a bucket is originating from a single geographic area, suggest pinning to the nearest region for lower latency.
- **Replication gap warning**: If region assignment rules cannot be fully satisfied due to insufficient nodes in a region, the tenant is warned with a specific message and the affected blobs are identified.
- **Unused credential warning**: Credentials that have not been used in 90 days are surfaced for review and potential revocation.
