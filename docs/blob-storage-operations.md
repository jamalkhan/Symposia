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

## Upgrading to this release (release blocker)

**Stop every old gateway before starting a new one on the same metadata database.** Never run two releases against one metadata database at the same time. From this release, deleted objects are kept in `object_versions` and erasure-coded objects in `object_shards`. An older gateway doesn't know those tables. It would treat their blobs as unreferenced and delete them, which loses data that can no longer be restored. The new schema stamps `PRAGMA user_version = 2`, so a later release can refuse to start against a database newer than itself.

## Redundancy mode

Every gateway in a deployment must run the same `Redundancy:Mode` and redundancy settings. Gateways configured differently place, size quorums for, and repair the same data differently.

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

## Placement, penalties and repair

In network mode, placement follows `redundancy-and-data-integrity.md` (BlobStorage #8):

- **Fault domains.** No two copies of a blob ever share a public IP. The gateway uses the address it connects to, falling back to the node's `StorageNode:PublicIp`. Copies avoid sharing a /24 subnet, `StorageNode:Asn` or `StorageNode:FacilityId` where possible. Each such rule bent is recorded on the object as a placement gap (see `?meta`), and the repair scan improves it once a compliant node exists.
- **Region precedence.** The object's `x-symposia-regions` header on PUT, then the longest matching folder rule (`PUT /v1/buckets/{b}/regions?prefix=…`), then the bucket's assignment, then global.
- **Erasure coding.** `PUT /v1/buckets/{b}/scheme {"scheme":"rs:4+2"}` stores new single-PUT objects as Reed–Solomon shards. Multipart uploads into such a bucket are still stored as full copies.
- **Penalties.** `POST /admin/nodes/penalty {"node":"…","stage":0-4}` sets a node's penalty stage until the on-chain penalty system feeds it. Stage 1 and 2 add one and two extra copies of the node's blobs. Stage 3 takes the node out of placement and reads and migrates its data. Stage 4 audits its blobs, and any left short of target are repaired at P0.
- **Repair tiers.** P0 ≤ 5 min, P1 ≤ 30 min, P2 ≤ 2 h, P3 ≤ 24 h. `GET /admin/repair/sla?hours=24` reports compliance per tier. `GET /admin/placement/ip-violations` should always return `count: 0`.
- **Bandwidth.** Repair traffic is capped per node at `Redundancy:BandwidthBudgetPercent` (25 %) of its `StorageNode:UploadBytesPerSecond`, or of `Redundancy:DefaultNodeUploadBytesPerSecond` when the node reports none. Stage 3 and 4 nodes get `RecoveryBandwidthBudgetPercent`.
- **Challenges.** About every `Redundancy:ChallengeIntervalSeconds`, at random, a random replica must prove a random 64 KiB leaf against the Merkle root recorded at write time. Failures show in `/admin/nodes` as `challengeFailures` and `recommendedPenaltyStage`.

## Garbage collection

BlobStorage #16. A delete is a **soft delete**. The object disappears from the API at once, but its bytes stay for the bucket's recovery window (`Gateway:Gc:RecoveryDays`, default 30). The bucket lifecycle may set 1–365 days for ePHI. Within the window, `POST /v1/buckets/{b}/objects/{k}?restore` brings the object back. Storage is billed, and counts toward quota, until GC confirms deletion from every replica. That moment is recorded as a `blob.physically_deleted` event.

- `PUT /v1/buckets/{b}/lifecycle` sets `versioning`, `maxVersions`, `maxVersionAgeDays`, `minRetentionDays`, `recoveryDays` and `multipartTtlHours` (1–720).
- `POST /v1/buckets/{b}/objects/{k}?retention {"lockedUntil": "...", "legalHold": true}`: GC never deletes a locked or held object, at any age.
- GC deletes in priority order: replicas on slashed or terminated nodes first, then purges and expired multipart uploads, then stale replicas and orphans. At most `Gc:DeletesPerSecondPerNode` deletions go to each node per second.
- A blob a node holds that nothing references is deleted only after `Gc:OrphanGraceHours` (48) from first sighting, **and** once the chain confirms it has no record. That confirmation doesn't exist yet, so orphans are detected and aged but never deleted. The node itself never deletes them either; it records them for reconciliation.
- `GET /v1/gc` (tenant) and `GET /admin/gc` show objects pending physical deletion, their estimated purge time and placement warnings. `safetyRefusals` in `/admin/gc` should stay at 0. Anything else means GC was asked to delete protected data and refused.

## Tenants and credentials

BlobStorage #3. The **Credential Authority** (`BlobStorage/Symposia.BlobStorage.CredentialAuthority`) is the source of truth for tenants, credentials, bucket policies, object ACLs and the credential audit log. It needs Postgres. Configure:

| Setting | Purpose |
|---|---|
| `CredentialAuthority:ConnectionString` | Postgres. Run it with standard HA; tenant data has no other copy. |
| `CredentialAuthority:OperatorToken` | Platform operators: `POST /v1/tenants/` and `POST /v1/tenants/{id}/suspend|reactivate|delete|purge`. |
| `CredentialAuthority:ServiceToken` | Gateways only. Every `/internal/*` call and the Watch stream need it. |
| `CredentialAuthority:MasterKey` | 32 bytes, base64. Encrypts credential secrets at rest. |

On each gateway, set `Gateway:CredentialAuthority:Url` and `ServiceToken`. The gateway caches the Authority's state in memory. The Watch stream pushes each change, and a full pull every `PollSeconds` (default 15) covers stream outages, so a revocation reaches every gateway in at most one poll interval. A credential the cache doesn't know is refused. Credentials listed in `Gateway:Credentials` still work as tenant-wide managers of their tenant (development and bootstrap deployments). They exist only on the gateways that list them.

Creating a tenant returns its first admin credential, and the secret appears only in that response. Tenants then manage their own credentials through any gateway, signed with SigV4: `/v1/credentials` (issue, list), `/v1/credentials/{id}/revoke`, `/v1/credentials/{id}/rotate`, `/v1/policies/{bucket}[?key=]`, `/v1/audit` and `/v1/audit/verify`. A credential can never mint one broader than itself.

A bucket belongs to the tenant that created it. Any other tenant gets `403 CrossTenantAccessDenied` for it, the same answer as for a bucket that doesn't exist. On a single-tenant gateway, buckets from before this release are assigned to its one configured tenant at startup. With several tenants, assign them yourself first.

**Secrets at rest.** SigV4 needs the secret itself to check a signature, so secrets can't be stored only as a one-way hash. The Authority stores each one AES-GCM encrypted, alongside a SHA-256 hash, and never returns it after issuance. Gateways receive it over the service-token channel and hold it only in memory.

### Storage-node TLS pinning

Gateways pin each storage node to the certificate fingerprint that the node's key signed on chain (BlobStorage #142). Since #252 this pinning shares its rules with the Authority's mutual TLS. A node certificate is refused when:
- it isn't the pinned one;
- it has expired (no tolerance: the expiry is what the node's proof signs);
- its NotBefore date is more than `Gateway:StorageTlsClockSkewSeconds` ahead (default 300);
- it declares extended key usages without serverAuth.

Certificates the node creates itself always pass. Refusals are counted in `storage_node_tls_refusals_total{reason}`, where `reason` is one of `fingerprint_mismatch`, `expired`, `not_yet_valid`, `missing_usage` or `no_certificate`. Each refusal is also logged as a warning with the node ID, at most once every five minutes per node and reason. The node's `LastProbeError` shows `tls: <reason>`. If you supply your own node certificate (`StorageNode:TlsCertificatePath`), it must either list serverAuth or carry no extended key usage at all.

### Credential sync, mutual TLS and master keys

BlobStorage #245. Gateways pull only what changed (`/internal/changes`). A credential's secret crosses the wire only when the credential is created. A full snapshot is pulled only at startup, every `Gateway:CredentialAuthority:ReconcileMinutes` (default 10, jittered), and when the Authority answers 410. The Authority returns 410 when a gateway is further behind than `ChangeLogRetentionDays` (default 7), when the Authority's database has been restored, or when the delta would exceed `MaxDeltaEntities`.

**Turning on mutual TLS for `/internal/*` and Watch:**
1. Give the Authority a server certificate (`CredentialAuthority:InternalTls:CertificatePath`). The internal API then listens on `InternalTls:Port` (default 5443). `/v1` and `/health` stay on `OperatorPort`, or on whatever `Kestrel:Endpoints` declares.
2. Set `InternalTls:Mode` to `Permissive`. Outside Development this is already the default, and an Authority with no certificate configured refuses to start rather than serve the internal API without TLS. Only an explicit `Mode: Off` turns it off. The mode and port are read once at startup, so changing either needs a restart. The allowlist reloads live.
3. Give each gateway a client certificate (`Gateway:CredentialAuthority:ClientCertificatePath`). Pin the Authority with `ServerFingerprints`, or trust it through a CA with `ServerCaPath`. Add each gateway's fingerprint to `InternalTls:ClientFingerprints`. A fingerprint is the hex SHA-256 of the certificate's DER, the same format storage-node pins use. Colons and upper case (as `openssl x509 -fingerprint -sha256` prints them) are accepted. Anything that isn't 64 hex digits fails startup. A certificate is refused after its NotAfter date, and before its NotBefore date minus a clock-skew allowance (`ClockSkewSeconds` on both ends, default 300). It is also refused when it declares extended key usages without serverAuth (for the Authority) or clientAuth (for gateways).
4. Watch `authority_internal_requests_without_cert_total`. When it stays at 0, set `Mode` to `Enforced`. Production must run `Enforced`.

**Rotating the master key:**
0. Once, after every Authority replica runs this release, set `CredentialAuthority:SealFormat` to `Bound` on all of them. Until then the Authority seals new secrets in the previous release's format, so a rollback can still read them. While the format is `Legacy`, an `ActiveKey` other than `k1` fails startup and `POST /v1/keys/reseal` answers 409 `SealFormatLegacy`. Don't roll back to the previous release after switching.
1. On every Authority replica, add the new key to `CredentialAuthority:MasterKeys`, for example `"k2": "<base64 32 bytes>"`, but don't make it active yet. The legacy single `MasterKey` is read as `k1`.
2. On every replica, set `ActiveKey` to `k2`.
3. Run `POST /v1/keys/reseal` with the operator token. Watch `GET /v1/keys` until `secretsByKey` shows only `k2`. The job is batched and resumable, and runs one at a time across replicas.
4. Remove `k1`. An Authority refuses to start while any stored secret still names a key it doesn't have.

A secret that can't be opened (corrupt, or sealed under a key that has since been lost) never blocks the rest of the system. The snapshot leaves that credential out, so the credential fails closed on its own, and the Authority counts it in `unreadableSecrets` on `GET /v1/keys`. The re-seal marks it `bad:<label>` in `secretsByKey` and carries on. A quarantined row doesn't count as using its key. Re-issue that credential. Alert on `unreadableSecrets` above 0, and don't remove a key while `secretsByKey` still shows `bad:<that key>` rows that haven't been reviewed.

After restoring the Authority's database, expect each gateway to take a full snapshot. A gateway that was ahead of the restored version gets 410 `AheadOfAuthority` and installs the restored state. There is one blind spot: if new writes carry the restored database past a gateway's version before that gateway polls, the gap goes undetected. The next reconcile (`ReconcileMinutes`) closes it. To close it at once, restart the gateways after a restore.

The re-seal lock, the migration lock and the state-version row lock are Postgres session or transaction locks. Connect the Authority directly or through session pooling. Transaction-mode PgBouncer would break the re-seal's advisory lock.

Make the new key active only after every replica has it, or one replica will seal secrets that another can't open.
