# Queue and Pub/Sub

## Overview

The platform requires a durable, high-throughput event bus to support:

- **Email delivery pipeline events** — `email_sent`, `email_opened`, `email_clicked`, `email_bounced`, etc. emitted inline by the delivery pipeline and consumed by the analytics, segmentation, and automation layers
- **Web tracking events** — page views, purchases, custom events emitted by the JS tracker and consumed by the contact event store and segmentation engine
- **Automation triggers** — "when a contact is added to segment X, enqueue a campaign send" and similar event-driven workflow hooks (see [Workflow Orchestration](../Todo.md))
- **Compliance operations** — unsubscribe requests, deletion requests, consent grant/revocation — these must be durable and must survive node failure; dropping a deletion request is a compliance violation
- **Cross-service fan-out** — a single event (e.g., `contact.unsubscribed`) may need to be consumed by the suppression list updater, the event integrity archiver, the analytics pipeline, and an automation trigger simultaneously

---

## Technology: NATS with JetStream

**NATS with JetStream** is the recommended message broker for the platform.

| Consideration | Reasoning |
|---|---|
| **Operational footprint** | NATS is a single binary with no external dependencies (no Zookeeper, no KRaft, no separate schema registry). Node runners already face operational complexity from running blockchain and storage nodes; a lightweight broker reduces that burden. |
| **Distributed-first design** | NATS was designed for cloud-native and distributed deployments. It handles clustering, failover, and multi-datacenter replication natively — well-matched to a decentralized node network. |
| **JetStream persistence** | JetStream (NATS's persistence layer) provides durable streams, at-least-once delivery, consumer acknowledgment, and replay — the properties needed for compliance-critical events like deletion requests. |
| **.NET client** | First-class .NET/C# client library, consistent with the existing codebase. |
| **Subject-per-tenant isolation** | NATS subjects are strings (`sym.tenant_abc.events.email_sent`). Tenant isolation is built into the subject hierarchy and enforced by NATS authorization policies — no shared-queue cross-contamination risk. |
| **Throughput** | NATS core can sustain millions of messages/second; JetStream is appropriate for the sustained event volumes this platform will see at scale. |

Kafka remains a viable alternative if operational complexity is acceptable. Redis Streams is appropriate for lower-volume use cases but does not have the clustering and durability profile needed for compliance-critical events. NATS with JetStream is the recommendation for v1.

---

## Subject Hierarchy

NATS subjects follow a hierarchical dot-separated naming convention. All Symposia platform subjects are rooted at `sym.`:

```
sym.{tenant_id}.events.email.sent
sym.{tenant_id}.events.email.opened
sym.{tenant_id}.events.email.clicked
sym.{tenant_id}.events.email.bounced
sym.{tenant_id}.events.email.complained
sym.{tenant_id}.events.email.unsubscribed
sym.{tenant_id}.events.web.pageview
sym.{tenant_id}.events.web.purchase
sym.{tenant_id}.events.web.custom
sym.{tenant_id}.contact.created
sym.{tenant_id}.contact.updated
sym.{tenant_id}.contact.deleted
sym.{tenant_id}.compliance.unsubscribe_requested
sym.{tenant_id}.compliance.deletion_requested
sym.{tenant_id}.compliance.consent_granted
sym.{tenant_id}.compliance.consent_revoked
sym.{tenant_id}.compliance.identity_verification_lapsed   ← T1/T2 claim expired after 13 months; contact drops to unclaimed
sym.platform.integrity.batch_committed        ← platform-level, not tenant-scoped
```

Wildcards work as expected: `sym.{tenant_id}.events.>` subscribes to all events for a tenant. `sym.*.compliance.>` (platform-only) subscribes to all compliance events across tenants.

---

## JetStream Streams

JetStream streams are named, durable storage buckets over one or more subjects. The platform defines the following streams:

| Stream | Subjects | Retention | Replicas | Purpose |
|---|---|---|---|---|
| `EVENTS_{tenant_id}` | `sym.{tenant_id}.events.>` | 7 days | 3 | All contact events for one tenant. Hot layer before archival to Postgres/blob. |
| `COMPLIANCE_{tenant_id}` | `sym.{tenant_id}.compliance.>` | 90 days | 3 | Compliance operations. Longer retention — these must survive any downstream processing outage. |
| `CONTACTS_{tenant_id}` | `sym.{tenant_id}.contact.>` | 7 days | 3 | Contact CRUD events. |
| `PLATFORM_INTEGRITY` | `sym.platform.integrity.>` | 365 days | 5 | Merkle commitment records. Higher replica count; this is the audit trail. |

Streams are provisioned per tenant at account creation. The `COMPLIANCE_*` stream's 90-day retention ensures that even if the downstream compliance processor is down for weeks, no deletion or unsubscribe request is lost.

---

## Producers

| Producer | Subjects Published | Notes |
|---|---|---|
| **Delivery pipeline** (OutboundRelayWorker + DSN processor) | `sym.{tenant}.events.email.*` | Emits inline with the delivery state transition — not async. See [Outbound Email Delivery — Event Generation](../Messaging/outbound-email-delivery.md#event-generation). |
| **JS tracker / tracking pixel** | `sym.{tenant}.events.web.*` | High-frequency; batched at the edge before publishing to reduce per-message overhead. |
| **Contact API** | `sym.{tenant}.contact.*` | Any create/update/delete on a contact record publishes an event. |
| **Compliance API** | `sym.{tenant}.compliance.*` | Unsubscribe, deletion, and consent operations publish here immediately before any downstream processing. The publish is the durable record of intent. |
| **Integrity archiver** (platform service) | `sym.platform.integrity.batch_committed` | Publishes after each hourly Merkle commitment is written to the chain. See [Event Integrity](./event-integrity.md). |

---

## Consumers

| Consumer | Subscribes To | Behavior |
|---|---|---|
| **Contact event writer** | `sym.{tenant}.events.>` | Writes events to `marketing.contact_events` (Postgres). Acknowledges after successful write. At-least-once; idempotent on `event_id`. |
| **Suppression list updater** | `sym.{tenant}.events.email.bounced`, `sym.{tenant}.events.email.complained`, `sym.{tenant}.compliance.unsubscribe_requested` | Adds contact to suppression list. Idempotent. |
| **Segmentation engine** | `sym.{tenant}.events.>`, `sym.{tenant}.contact.>` | Real-time segment membership updates. Near-real-time, not guaranteed instant (eventual consistency for segment membership is acceptable). |
| **Event integrity archiver** | `sym.{tenant}.events.>`, `sym.{tenant}.compliance.>` | Collects events into hourly batches for Merkle commitment and blob archival. See [Event Integrity](./event-integrity.md). |
| **Automation trigger evaluator** | `sym.{tenant}.events.>`, `sym.{tenant}.contact.>`, `sym.{tenant}.compliance.>` | Evaluates automation rules against incoming events. Publishes new campaign sends or workflow steps when rules match. (Workflow orchestration spec TBD — see [Todo.md](../Todo.md).) |
| **Analytics pipeline** | `sym.{tenant}.events.>` | Feeds the analytical layer (DuckDB/ClickHouse — see [Analytics](../Analytics/todo-notes.md)). |
| **Compliance processor** | `sym.{tenant}.compliance.deletion_requested` | Executes the deletion/anonymization workflow across the contact database. See [Right to Delete](../Identity/right-to-delete.md). Long-running; uses JetStream's `MaxAckPending` to prevent overload. |

---

## Delivery Guarantees and Idempotency

All consumers operate on **at-least-once delivery** — NATS JetStream will redeliver an unacknowledged message. Consumers must be idempotent: processing the same message twice should produce the same outcome as processing it once.

The mechanism: every event carries a unique `event_id` (UUID v7 — time-ordered, important for Merkle batch ordering). Consumers that write to Postgres use `INSERT ... ON CONFLICT (event_id) DO NOTHING`. Consumers that update state (suppression list, segment membership) check for the existing record before writing.

**Compliance events** (`deletion_requested`, `unsubscribe_requested`) get special treatment: they are published to JetStream *before* the API returns a 200 to the caller. The caller's success response means "your request is durably recorded." Downstream processing (actually deleting the contact record, actually updating the suppression list) is async, but the intent is committed. This means a compliance request can never be silently dropped due to a downstream service being unavailable.

---

## Tenant Isolation and Authorization

NATS authorization is configured so that:
- A marketer's application credentials can publish and subscribe only to `sym.{their_tenant_id}.*`
- Platform services (delivery pipeline, integrity archiver, compliance processor) have broader credentials scoped to their specific subject patterns
- No marketer credential can subscribe to another marketer's subjects
- The `sym.platform.*` namespace is write-accessible only to platform services, not tenant credentials

Authorization is enforced at the NATS server level using NKeys (Ed25519 keypairs) and operator/account/user hierarchy — consistent with the platform's existing use of ECDSA-signed inter-node requests.

---

## Integration with the Event Integrity Layer

The pub/sub layer is the source of record for events as they happen. Events flow:

```
Producer publishes event
        │
        ▼
NATS JetStream (hot — 7 days)
        │
        ├──▶ Contact event writer → Postgres marketing.contact_events (warm — months)
        │
        └──▶ Event integrity archiver → hourly batch → blob storage (cold — years)
                                                    └──▶ Merkle root → blockchain (permanent)
```

See [Event Integrity](./event-integrity.md) for the full archival and commitment pipeline.
