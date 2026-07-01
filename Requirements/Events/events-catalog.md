# Events Catalog

This document catalogs all named events identified across the Symposia platform requirements. Events are grouped by domain. Each entry notes the producer, consumer(s), and a brief description.

Naming conventions and delivery infrastructure are described at the bottom.

---

## Platform Pub/Sub Events

Source: [queue-and-pubsub.md](../Platform/queue-and-pubsub.md)

NATS JetStream subjects follow the pattern `sym.{tenant_id}.events.*` for tenant-scoped events and `sym.platform.*` for platform-level events.

### Email Lifecycle

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `email.sent` | OutboundRelayWorker / DSN processor | Analytics, segmentation, automation | Email handed off to MX delivery |
| `email.opened` | Email tracking infrastructure | Analytics, segmentation, automation | Tracking pixel fetched; includes `open_type: human` or `machine` |
| `email.clicked` | Email tracking infrastructure | Analytics, segmentation, automation | Tracked link in email was clicked |
| `email.bounced` | DSN processor | Suppression list updater, analytics, automation | Terminal 5xx or soft-bounce timeout |
| `email.complained` | DSN processor / FBL processor | Suppression list updater, analytics, automation | FBL complaint or one-click unsubscribe complaint |
| `email.unsubscribed` | One-click unsubscribe processor | Suppression list updater, analytics, automation | One-click unsubscribe token processed |

### Web Tracking

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `web.pageview` | JS tracker / tracking pixel | Contact event writer, segmentation engine, analytics | Page view tracked on marketer website |
| `web.purchase` | JS tracker / tracking pixel | Contact event writer, segmentation engine, analytics | Purchase event tracked on marketer website |
| `web.custom` | JS tracker / tracking pixel | Contact event writer, segmentation engine, analytics | Marketer-defined custom event tracked on website |

### Contact Lifecycle

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `contact.created` | Contact API | Contact event writer, segmentation engine, automation | Contact record created |
| `contact.updated` | Contact API | Contact event writer, segmentation engine, automation | Contact record updated |
| `contact.deleted` | Contact API | Contact event writer, segmentation engine, automation | Contact record deleted |

### Compliance

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `compliance.unsubscribe_requested` | Compliance API / one-click unsubscribe processor | Suppression list updater, event integrity archiver, analytics, automation | Unsubscribe request received; included in hourly Merkle commitments |
| `compliance.deletion_requested` | Compliance API | Compliance processor, event integrity archiver, analytics | Right-to-delete request received; included in Merkle commitments |
| `compliance.consent_granted` | Compliance API | Event integrity archiver, analytics | Consent grant recorded; included in Merkle commitments |
| `compliance.consent_revoked` | Compliance API | Event integrity archiver, analytics | Consent revocation recorded; included in Merkle commitments |
| `compliance.identity_verification_lapsed` | Identity system | Profile portal, segmentation engine | T2 identity claim expired after 13 months without re-verification |

### Platform Integrity

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `platform.integrity.batch_committed` | Event integrity archiver | Blockchain smart contracts | Hourly Merkle root written to on-chain log |

---

## Blob Storage Events

Source: [blob-event-notifications.md](../BlobStorage/blob-event-notifications.md), [garbage-collection.md](../BlobStorage/garbage-collection.md)

Delivered as webhook HTTP POSTs (HMAC-SHA256 signed) to tenant-configured endpoints. Retry schedule: 30s, 2m, 10m, 1h, 6h. Dead-letter retention: 7 days.

### Blob Lifecycle

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `blob.created` | Blob gateway / storage nodes | Tenant webhooks, analytics | New blob written; all minimum replicas confirmed |
| `blob.updated` | Blob gateway | Tenant webhooks | Existing blob overwritten with a new version |
| `blob.deleted` | Blob gateway / garbage collector | Tenant webhooks | Blob soft- or hard-deleted |
| `blob.restored` | Blob gateway | Tenant webhooks | Soft-deleted blob restored within the recovery window |
| `blob.metadata_updated` | Blob gateway | Tenant webhooks | User-defined metadata changed without a content change |
| `blob.copy_completed` | Blob gateway | Tenant webhooks | Server-side copy operation completed |
| `blob.multipart_completed` | Blob gateway | Tenant webhooks | Multipart upload assembled and blob is available |
| `blob.multipart_aborted` | Garbage collector | Tenant webhooks | Multipart upload explicitly aborted or expired (7-day default TTL) |
| `blob.tier_changed` | Workload routing / performance tier system | Tenant webhooks | Blob's primary serving tier promoted or demoted |

### Replication Health

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `blob.replica_lost` | Replication system | Tenant webhooks, replication engine | Replica went offline; repair triggered |
| `blob.replica_repaired` | Replication system | Tenant webhooks | Lost replica successfully restored on a new node |
| `blob.below_minimum_replicas` | Replication system | Tenant webhooks, P0 alerts | Live replica count dropped below the configured minimum |
| `blob.repair_complete` | Replication system | Tenant webhooks | Repair complete; blob back to target replica count |

---

## Billing & Account Events

Source: [retention-and-billing.md](../Platform/retention-and-billing.md)

Delivered via tenant webhook and email alert.

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `account.credit_low` | Billing system | Tenant webhooks, email alerts | Credit balance crossed the low threshold (~30 days remaining) |
| `account.credit_critical` | Billing system | Tenant webhooks, email alerts | Credit balance crossed the critical threshold (~7 days remaining) |
| `account.credit_zero` | Billing system | Tenant webhooks, email alerts | Credit balance reached zero; writes suspended |
| `account.soft_suspended` | Billing system | Tenant webhooks, email alerts | Account entered soft suspension; reads are rate-limited |

---

## Contact Database Events

Source: [contact-database.md](../MarketingData/contact-database.md)

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `contact.erased` | Deletion processor | Marketer webhooks, external integrations | Right-to-delete fulfilled; signals marketer to cascade deletion in their own systems |

---

## Tracking System Events

Source: [event-schema.md](../Tracking/event-schema.md), [tracking-architecture.md](../Tracking/tracking-architecture.md)

These events are written directly to `marketing.contact_events` by the JS tracker SDK and email pipeline. They overlap intentionally with pub/sub email events — email events are dual-recorded both into pub/sub and inline to the contact event store.

### Behavioural / Web

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `page_view` | JS tracker | Contact event store, segmentation engine, analytics | Page load or SPA navigation |
| `page_exit` | JS tracker | Contact event store, analytics | Page unload; includes time-on-page duration |
| `scroll` | JS tracker | Contact event store, analytics | Scroll milestone reached (25%, 50%, 75%, 90%, 100%) |
| `click` | JS tracker | Contact event store, analytics | Click on `<a>` or `<button>` element |
| `form_submit` | JS tracker | Contact event store, analytics | Form submitted; field names captured, not values |
| `identify` | JS tracker | Contact event store, segmentation engine | Visitor identified with an email address; retroactively links prior anonymous events |
| `custom` | JS tracker (marketer-instrumented) | Contact event store, segmentation engine, analytics | Marketer-defined custom event |

### Consent

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `cookie_consent_shown` | JS tracker | Contact event store, compliance | Marketer's cookie consent banner rendered |
| `cookie_consent_recorded` | JS tracker | Contact event store, compliance | Visitor made a consent decision in the marketer's banner |
| `symposia_platform_cookie_consent_shown` | JS tracker | Contact event store, compliance | Symposia fallback cookie banner shown (no marketer banner detected) |
| `symposia_platform_cookie_consent_recorded` | JS tracker | Contact event store, compliance | Visitor decision recorded in Symposia fallback banner |

### Email (Tracking Store Mirror)

These duplicate the pub/sub email events above and are written inline to the contact event store.

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `email_sent` | Email delivery pipeline | Contact event store, analytics | Email sent to recipient |
| `email_delivered` | DSN processor | Contact event store, analytics | Receiving MX accepted the message |
| `email_opened` | Email tracking infrastructure | Contact event store, analytics | Email tracking pixel fetched |
| `email_clicked` | Email tracking infrastructure | Contact event store, analytics | Tracked link in email clicked |
| `email_bounced` | DSN processor | Contact event store, analytics | Bounce received |
| `email_complained` | DSN processor / FBL | Contact event store, analytics | Complaint received |
| `email_unsubscribed` | One-click unsubscribe processor | Contact event store, analytics | Unsubscribe action recorded |

### Ecommerce

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `product_viewed` | JS tracker (marketer-instrumented) | Contact event store, analytics | Product detail page viewed |
| `cart_add` | JS tracker (marketer-instrumented) | Contact event store, analytics | Item added to cart |
| `cart_remove` | JS tracker (marketer-instrumented) | Contact event store, analytics | Item removed from cart |
| `cart_viewed` | JS tracker (marketer-instrumented) | Contact event store, analytics | Cart page viewed |
| `checkout_started` | JS tracker (marketer-instrumented) | Contact event store, analytics | Checkout flow initiated |
| `purchase` | JS tracker (marketer-instrumented) | Contact event store, analytics | Purchase completed |
| `refund` | JS tracker (marketer-instrumented) | Contact event store, analytics | Refund processed |

---

## Network Node Events

Source: [node-runner-incentives-and-penalties.md](../Network/node-runner-incentives-and-penalties.md)

These are blockchain-recorded events (OP Stack L3) that serve as the audit trail for node reputation and rewards.

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `node.stage_transition` | Node monitoring system | On-chain log, node operator alerts | Node entered a penalty stage (stages 1–4) |
| `node.slashed` | Slashing system | On-chain log, node operator alerts | Stake slash executed against a node |
| `node.capacity_reduced` | Node operator | On-chain log, replication engine | Node operator reduced their offered storage capacity |
| `node.reward_withheld` | Reward distribution system | On-chain log, node accounting | Rewards held in claimable reserve due to low heartbeat compliance |

---

## Verifier Node Events

Source: [verifier-nodes.md](../Network/verifier-nodes.md)

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `verification.attestation` | Verifier nodes | Blockchain, node registry | Geographic location and performance metrics attested |
| `verification.failed` | Verifier nodes | Blockchain, node registry | Region claim verification failed; node marked unverified |
| `verification.slashed` | Slashing system | On-chain log, verifier accounting | Verifier stake slashed for fraudulent attestation |

---

## Region Events

Source: [region-identification-and-verification.md](../Network/region-identification-and-verification.md)

| Event | Producer | Consumer(s) | Description |
|---|---|---|---|
| `node.region_claimed` | Storage node | Verifier pool, blockchain | Node submitted a signed region claim with supporting evidence |
| `node.region_verified` | Verifier nodes | Blockchain, node registry | Region claim confirmed via independent measurements |
| `node.region_update_requested` | Node operator | Verifier pool, blockchain | Node requested a region update after a physical move |

---

## Architectural Notes

### Delivery Mechanisms

- **NATS JetStream (pub/sub):** Used for all platform-tier messaging events (`sym.{tenant_id}.*`, `sym.platform.*`). At-least-once delivery; consumers deduplicate using `event_id` (UUID v7).
- **Webhooks (HTTP POST):** Used for blob storage and billing events delivered to tenant-configured endpoints. HMAC-SHA256 signed. Retry schedule: 30s → 2m → 10m → 1h → 6h. Dead-letter retention: 7 days.
- **Inline writes:** Email tracking events are dual-recorded — emitted to pub/sub AND written directly to `marketing.contact_events` in the delivery pipeline (not async).
- **Blockchain (OP Stack L3):** Node penalties, slashing, verification attestations, and Merkle batch commits are recorded on-chain as the canonical audit trail.

### Compliance Event Retention

Compliance events (`compliance.*`) are retained for 90 days in a dedicated NATS stream (`COMPLIANCE_{tenant_id}`). They are included in hourly Merkle commitments to the blockchain for tamper-evidence and are covered by the right-to-delete exemption for legal record keeping.

### Event Counts by Domain

| Domain | Count |
|---|---|
| Platform pub/sub (email, web, contact, compliance, integrity) | 17 |
| Blob storage (lifecycle + replication) | 13 |
| Billing & account | 4 |
| Contact database | 1 |
| Tracking system (behavioural, consent, email mirror, ecommerce) | 24 |
| Network node | 4 |
| Verifier node | 3 |
| Region | 3 |
| **Total** | **69** |
