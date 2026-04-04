# Basemail v1 Node API

This file describes the signed HTTP protocol used between Basemail nodes and between network gateways and clients.

## Principles

- all node-to-node traffic is signed
- message bodies and mailbox state remain offchain
- any attached node may serve reads for any mailbox
- replica acknowledgements are explicit and signed

## Authentication Model

Every node has:

- `nodeId`
- operator wallet on Base
- node signing keypair
- registered capability manifest

Required headers on signed node requests:

```text
X-Basemail-Node: <nodeId>
X-Basemail-Timestamp: <unix-seconds>
X-Basemail-Nonce: <uuid-or-random>
X-Basemail-Signature: <base64 signature>
X-Basemail-Key-Id: <public-key-id>
```

Signature input should canonically cover:

- method
- path
- timestamp
- nonce
- content hash

Reject requests when:

- timestamp drift is too large
- nonce is replayed
- signature is invalid
- node is unknown, inactive, or jailed

## Canonical Types

### Canonical Message Package

```json
{
  "mailboxId": "0xmailbox",
  "messageId": "0xmessage",
  "contentHash": "0xhash",
  "envelopeFrom": "sender@example.com",
  "envelopeRecipients": ["jamal@symposia.com"],
  "headers": [
    { "name": "Subject", "value": "Hello" }
  ],
  "plainTextBody": "Hello",
  "htmlBody": "<p>Hello</p>",
  "receivedAtUtc": "2026-04-04T12:00:00Z"
}
```

### Replica Acknowledgement

```json
{
  "messageId": "0xmessage",
  "nodeId": "0xnodeA",
  "stored": true,
  "storageProofStub": "0xproofstub",
  "timestampUtc": "2026-04-04T12:00:05Z",
  "signature": "..."
}
```

## Node Status

### `GET /network/status`

Purpose:

- peer health
- capability advertisement
- operator diagnostics

Response:

```json
{
  "nodeId": "0x...",
  "operator": "0x...",
  "capabilities": {
    "smtpIngress": true,
    "mailStorage": true,
    "mailIndex": true,
    "webGateway": true
  },
  "health": {
    "uptimeScore": 0.9981,
    "storageAvailableBytes": 1234567890,
    "appMemory": {
      "workingSetBytes": 123,
      "privateMemoryBytes": 456
    },
    "appCpu": {
      "totalProcessorTimeMs": 789
    }
  }
}
```

## Message Ingress

### `POST /network/messages/ingest`

Purpose:

- accept canonical message package
- resolve candidate replicas
- start replication workflow

Request body:

```json
{
  "mailboxId": "0xmailbox",
  "messageId": "0xmessage",
  "contentHash": "0xhash",
  "envelopeFrom": "sender@example.com",
  "envelopeRecipients": ["jamal@symposia.com"],
  "headers": [
    { "name": "Subject", "value": "Hello" }
  ],
  "plainTextBody": "Hello",
  "htmlBody": "<p>Hello</p>",
  "receivedAtUtc": "2026-04-04T12:00:00Z"
}
```

Response:

```json
{
  "accepted": true,
  "messageId": "0xmessage",
  "selectedReplicaNodes": ["0xnodeA", "0xnodeB"]
}
```

Acceptance rule:

- ingress is not complete until at least 2 replica acknowledgements are collected

## Replica Write

### `POST /network/messages/{messageId}/replicas`

Purpose:

- store message on a replica node

Request body:

```json
{
  "mailboxId": "0xmailbox",
  "contentHash": "0xhash",
  "messageBlob": "<base64 or chunk ref>",
  "metadata": {
    "envelopeFrom": "sender@example.com",
    "deliveredAddresses": ["jamal@symposia.com"]
  }
}
```

Response:

```json
{
  "stored": true,
  "messageId": "0xmessage",
  "storageProofStub": "0xproofstub"
}
```

## Message Read

### `GET /network/messages/{messageId}`

Purpose:

- fetch canonical stored message

Response:

```json
{
  "messageId": "0xmessage",
  "contentHash": "0xhash",
  "rawMessage": "....",
  "metadata": {
    "subject": "Hello",
    "receivedAtUtc": "2026-04-04T12:00:00Z"
  }
}
```

## Mailbox Index

### `GET /network/mailboxes/{mailboxId}/index`

Purpose:

- fetch a mailbox summary view

Response:

```json
{
  "mailboxId": "0xmailbox",
  "indexVersion": 42,
  "messages": [
    {
      "messageId": "0xmessage",
      "threadId": "0xthread",
      "receivedAtUtc": "2026-04-04T12:00:00Z",
      "subject": "Hello",
      "preview": "Hello"
    }
  ]
}
```

### `POST /network/mailboxes/{mailboxId}/commit`

Purpose:

- publish updated mailbox index root

Request:

```json
{
  "indexVersion": 42,
  "indexRoot": "0xindexroot",
  "replicaSet": ["0xnodeA", "0xnodeB"]
}
```

## Proof APIs

### `POST /network/proofs/storage`

Request:

```json
{
  "messageId": "0xmessage",
  "challengeId": "0xchallenge",
  "proof": "0xproofdata"
}
```

### `POST /network/proofs/uptime`

Request:

```json
{
  "epochId": 101,
  "observedNodeId": "0xnodeA",
  "availability": 0.998,
  "signedObservation": "0xsig"
}
```

## Gateway APIs

These are the user-facing APIs that the inbox client will eventually use instead of local-only filesystem access.

- `GET /api/network/mailboxes/{mailboxId}`
- `GET /api/network/mailboxes/{mailboxId}/messages?page=1&pageSize=25`
- `GET /api/network/threads/{threadId}`
- `GET /api/network/messages/{messageId}`
- `POST /api/network/compose`
- `POST /api/network/auth/wallet-challenge`
- `POST /api/network/auth/wallet-verify`

## Error Model

All endpoints should return a standard error envelope:

```json
{
  "error": {
    "code": "replica_unavailable",
    "message": "Replica node did not acknowledge storage in time.",
    "retryable": true
  }
}
```

## Versioning

Prefix network APIs with version headers first:

```text
X-Basemail-Protocol-Version: 1
```

If the protocol evolves materially, move to path versioning:

- `/network/v2/...`
- `/api/network/v2/...`
