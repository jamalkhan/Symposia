# Basemail v1

This document set describes the first protocolized version of Basemail as a Base-based network for hosted mail and future decentralized services.

The current codebase remains the single-node reference implementation:

- [SymposiaServer](/Users/jamal/Projects/Symposia/EmailProvider/SymposiaServer)
- [SymposiaInboxWeb](/Users/jamal/Projects/Symposia/EmailProvider/SymposiaInboxWeb)

Those projects are not being discarded. They are the starting point for:

- a Basemail node
- a network-aware inbox client
- a Base onchain coordination layer
- a replicated storage and retrieval protocol

## Document Map

- [contracts.md](/Users/jamal/Projects/Symposia/docs/basemail-v1/contracts.md)
  Smart contract interfaces and responsibilities for Base.
- [node-api.md](/Users/jamal/Projects/Symposia/docs/basemail-v1/node-api.md)
  Signed HTTP protocol for node-to-node and gateway interactions.
- [replication.md](/Users/jamal/Projects/Symposia/docs/basemail-v1/replication.md)
  Message placement, replication, retrieval, and state flow.
- [roadmap.md](/Users/jamal/Projects/Symposia/docs/basemail-v1/roadmap.md)
  A 12-week implementation plan mapped to this repo.

## v1 Goals

Basemail v1 should deliver:

1. a Base Sepolia control plane for node registration, mailbox ownership, and reward stubs
2. signed node-to-node APIs
3. replicated message storage on at least 2 nodes
4. mailbox routing by global `MailboxId`
5. inbox access from any attached node
6. simple uptime scoring offchain, committed onchain by epoch

## Explicit Non-Goals For v1

- raw email bodies onchain
- fully trustless storage proofs
- decentralized governance
- mainnet token launch
- generalized non-mail services in production

Those will be designed for, but not completed, in v1.

## Product Rules

- the native token is referred to as `$Basemail` for now
- mailbox owners ultimately pay for storage, bandwidth, privacy, and future services
- Basemail must support future service classes beyond mail
- mailbox owners may pay for private usage
- non-private mailboxes may participate in aggregated, anonymized telemetry products

## Design Principle

Base is for coordination, staking, commitments, pricing, rewards, and slashing.

Mail transport, storage, indexing, search, and UI remain offchain.
