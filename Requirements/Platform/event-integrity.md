# Event Integrity and Merkle Commitments

## The Problem

The platform stores billions of events about individuals — email sends, opens, clicks, unsubscribes, deletion requests, consent grants. These events are the ground truth for individual data rights: they are the evidence that an unsubscribe was honored, that a deletion request was received, that a consent grant was recorded.

Without a tamper-evidence mechanism, the platform's compliance claims rest entirely on "trust us, it's in our database." That is not a strong position when a regulator asks for proof, or when an individual wants to verify their own history.

**The answer is not to store events on the blockchain.** At the volumes this platform generates, storing raw events on-chain would add terabytes to chain state per month and expose sensitive marketing data to any node operator. Both outcomes are unacceptable.

The answer is **Merkle commitments**: store the events in encrypted blob storage (private, access-controlled, queryable) and periodically write a cryptographic fingerprint of each batch to the blockchain (public, immutable, 32 bytes). The fingerprint proves the events existed in their exact state at a specific point in time — without revealing any event content to the chain.

---

## What a Merkle Commitment Is

A Merkle tree is a structure where every piece of data is hashed individually, then those hashes are paired and hashed together, then those pairs are hashed together again — all the way up to a single hash at the top called the **Merkle root**.

```
                    [ Root Hash ]
                   /             \
          [ Hash AB ]           [ Hash CD ]
          /         \           /          \
    [ Hash A ]  [ Hash B ]  [ Hash C ]  [ Hash D ]
        |            |           |           |
    Event A      Event B     Event C     Event D
```

The root hash is a cryptographic fingerprint of all the data beneath it. Change anything — even one byte in Event B — and the root hash changes entirely.

A **Merkle commitment** means writing that root hash to the blockchain. Not the events — just the 32-byte fingerprint. The blockchain's immutability means the fingerprint can never be altered after the fact. The content-addressed blob storage means the events themselves can never be silently altered either (changing an event would change its storage address). Together: the event data is private and access-controlled, but its integrity is publicly verifiable.

---

## Why This Matters for Individual Rights

Consider a regulator or an individual asking: *"Prove that my unsubscribe request was recorded on June 30th and hasn't been altered since."*

Without Merkle commitments, the answer is: "Trust us, it's in our database." That is not sufficient for compliance purposes and is antithetical to the platform's trust model.

With Merkle commitments, the platform can:

1. Retrieve the unsubscribe event record from blob storage
2. Generate a **Merkle proof** — the minimal set of sibling hashes needed to reconstruct the path from that specific event up to the Merkle root
3. Point to the root hash that is recorded on the blockchain from that hour's commitment
4. The individual (or regulator, or auditor) verifies the math themselves — the proof either checks out or it doesn't

**No one needs to trust the platform.** The blockchain is a public, append-only place to store the fingerprint. The Merkle proof is deterministic math. If the event data was altered after the commitment was written, the proof will fail.

Individuals can run this verification themselves — through the Symposia profile portal or directly via the verification API — for any compliance-relevant event in their history: unsubscribe requests, deletion requests, consent grants and revocations.

---

## What Gets Committed

Not every event requires a Merkle commitment. The commitment pipeline covers events where tamper-evidence has compliance or rights-exercise value:

| Event Type | Commitment Priority | Reason |
|---|---|---|
| `compliance.unsubscribe_requested` | **Required** | Individual's right to opt out; must be provable |
| `compliance.deletion_requested` | **Required** | Individual's right to erasure; must be provable |
| `compliance.consent_granted` | **Required** | Legal basis for all downstream marketing |
| `compliance.consent_revoked` | **Required** | Withdrawal of consent; must be provable |
| `email.unsubscribed` | **Required** | One-click unsubscribe; ISP and legal audit trail |
| `email.sent` | Recommended | Proof of what was sent and when; FTC/CAN-SPAM |
| `email.bounced` | Recommended | Proof suppression was added when required |
| `email.complained` | Recommended | FBL complaint receipt; proof of action taken |
| `web.purchase` | Optional | Useful for dispute resolution; higher data volume |
| `web.pageview` | Not committed | Too high volume; low compliance value |
| `web.custom` | Marketer-configurable | Marketer may opt specific custom events in |

Compliance events (`compliance.*`) are committed in their own stream, separate from regular contact events, with higher replica count and longer hot retention. See [Queue and Pub/Sub](./queue-and-pubsub.md#jetstream-streams).

---

## The Commitment Pipeline

```
NATS JetStream (hot events)
        │
        ▼
Event Integrity Archiver (platform service)
        │
        ├─ Collects all committable events for tenant T over the past hour
        ├─ Sorts by event_id (UUID v7 — time-ordered, deterministic sort)
        ├─ Serializes each event to canonical JSON (deterministic field ordering)
        ├─ Computes SHA-256 hash of each serialized event
        ├─ Builds Merkle tree over all event hashes
        ├─ Encrypts event batch with tenant's data encryption key
        └─ Writes encrypted batch to blob storage (content-addressed)
                │
                └──▶ Writes Merkle commitment to blockchain:
                     {
                       tenant_id,
                       batch_start:    "2026-06-30T14:00:00Z",
                       batch_end:      "2026-06-30T15:00:00Z",
                       event_count:    4821,
                       merkle_root:    "sha256:a3f8...",
                       blob_address:   "blob://sym-events/tenant_abc/2026/06/30/14.batch",
                       committed_at:   "2026-06-30T15:01:12Z",
                       platform_sig:   "ecdsa:..."  ← platform signing key, not tenant
                     }
```

The commitment is written to the chain once per hour per tenant. Chain bloat per tenant: one transaction (~200 bytes) per hour = ~1.7 MB per tenant per year. For 1,000 tenants: ~1.7 GB/year across the entire platform. Negligible.

---

## Merkle Proof Generation

When an individual or auditor requests proof for a specific event:

1. Locate the event in `marketing.contact_events` by `event_id`
2. Identify which hourly batch it belongs to (by `occurred_at` timestamp → batch window)
3. Retrieve the encrypted batch from blob storage and decrypt with the tenant's key
4. Reconstruct the Merkle tree for that batch
5. Generate the proof path: the ordered list of sibling hashes from the event's leaf up to the root
6. Return: the event data, the proof path, and the on-chain commitment record for that batch

The verifier recomputes: `hash(event)` → walk up the proof path applying each sibling hash → arrive at a root hash → compare against the on-chain `merkle_root`. If they match, the event is authentic and unaltered.

---

## Individual Verification API

Individuals can verify any committed event in their own history through the profile portal or directly via the API. Authentication is required (Symposia account session or wallet signature).

```
GET  /identity/events/{event-id}/proof

Response:
{
  "event_id": "01j3k...",
  "event_type": "compliance.unsubscribe_requested",
  "occurred_at": "2026-06-30T14:23:11Z",
  "event_data": {
    "marketer_id": "tenant_walmart",
    "channel": "email",
    "contact_email_hash": "sha256:..."   // hashed in the proof; plaintext in the contact record
  },
  "merkle_proof": {
    "leaf_hash": "sha256:b7c2...",
    "path": [
      { "position": "right", "hash": "sha256:4f91..." },
      { "position": "left",  "hash": "sha256:2a08..." },
      { "position": "right", "hash": "sha256:9d33..." }
    ],
    "root": "sha256:a3f8...",
    "batch_window": "2026-06-30T14:00:00Z / 2026-06-30T15:00:00Z",
    "chain_tx": "0x7f3a..."   // blockchain transaction hash where commitment was recorded
  }
}
```

The response includes everything needed to verify the proof independently — the verifier does not need to call any other Symposia API. They need only the proof path and access to the blockchain to confirm the `chain_tx` contains the matching `root`.

The profile portal provides a human-readable version of this: "Your unsubscribe from Walmart email was recorded on June 30, 2026 at 2:23 PM. [Verify this on the blockchain →]"

---

## Scope of Individual Self-Verification

The following event types are surfaced in the individual's verification interface in the profile portal:

| What They Can Verify | Event Type |
|---|---|
| "I unsubscribed from this marketer's emails" | `compliance.unsubscribe_requested`, `email.unsubscribed` |
| "I requested deletion of my data from this marketer" | `compliance.deletion_requested` |
| "I granted consent to this marketer for email marketing" | `compliance.consent_granted` |
| "I revoked consent from this marketer" | `compliance.consent_revoked` |
| "This marketer sent me an email on this date" | `email.sent` |
| "This marketer received a complaint about an email they sent me" | `email.complained` |

Individuals cannot run proofs on other individuals' events. The API returns only events where `symposia_identity_id` matches the authenticated caller's identity.

---

## Blob Storage Layout

Event batches are stored in the platform's blob storage with a deterministic path structure:

```
sym-events/
  {tenant_id}/
    {year}/
      {month}/
        {day}/
          {hour}.batch          ← encrypted, content-addressed
          {hour}.batch.index    ← plaintext index of event_ids in this batch (for proof lookup without decrypting the full batch)
```

The `.batch.index` file maps `event_id → leaf_position` in the Merkle tree and is stored unencrypted (event IDs are UUIDs — not PII). This allows proof lookups to work without decrypting the full batch, which may contain thousands of events.

Batch files are immutable once written. The blob storage layer enforces this at the object level: batches are write-once, delete-never. Only the platform's integrity archiver service has write credentials to the `sym-events/` bucket; tenant application credentials are read-only to their own prefix.

---

## Retention

| Layer | Retention | Notes |
|---|---|---|
| NATS JetStream (hot) | 7 days | Redelivery window for consumer failures |
| Postgres `marketing.contact_events` (warm) | 2 years (configurable per tenant) | Queryable event history for segmentation, profile portal |
| Blob batch archives (cold) | 7 years | Matches typical regulatory audit retention requirements (GDPR audit trails, CAN-SPAM records) |
| Blockchain commitments | Permanent | 32-byte root hash per batch per tenant; trivial chain state |

---

## Open Questions

1. **Commitment frequency**: hourly is the default. Should compliance events (`deletion_requested`, `unsubscribe_requested`) trigger an immediate commitment rather than waiting for the next hourly batch? Near-real-time commitment would reduce the window in which a deletion request could theoretically be altered before commitment. Trade-off: more chain transactions per tenant.

2. **Tenant key rotation**: the blob batches are encrypted with the tenant's data encryption key. If the tenant rotates their key (or is offboarded), what happens to historical batches? Re-encryption of years of archived batches is expensive. Options: key escrow, envelope encryption (batch encrypted with a data key, data key encrypted with the tenant's master key — rotating only requires re-encrypting the data keys).

3. **Individual access to their own events without tenant cooperation**: currently, an individual can only get a Merkle proof for events in their history if the tenant's encryption key is available (the platform holds it in the custodial key management system). If a marketer is offboarded or their key is lost, can an individual still verify their compliance events? This may argue for compliance events being committed unencrypted (or encrypted with the individual's wallet key, not the tenant's key).

4. **Auditor access**: should regulators or court-ordered auditors be able to access blob archives directly (with a platform-issued auditor credential), or should all access go through the Merkle proof API? The former is simpler but gives auditors more data access than they strictly need for verification purposes.
