# Basemail v1 Roadmap

This is a 12-week implementation plan against the current Symposia repo.

The current codebase stays in place and is used as the single-node reference baseline.

## Phase 0: Repository Direction

Use these existing projects as the starting point:

- [SymposiaServer](/Users/jamal/Projects/Symposia/EmailProvider/SymposiaServer)
- [SymposiaInboxWeb](/Users/jamal/Projects/Symposia/EmailProvider/SymposiaInboxWeb)
- [SymposiaServer.IntegrationTests](/Users/jamal/Projects/Symposia/EmailProvider/SymposiaServer.IntegrationTests)

Do not rewrite from scratch yet.

## Weeks 1-2: Network Foundations

Goals:

- add node identity
- add signed HTTP request model
- separate mailbox routing from local-address-only assumptions

Tasks:

1. add `NodeId`, operator wallet address, and node signing keys
2. define peer configuration and capability manifest
3. implement signed request verification middleware
4. introduce global `MailboxId` routing abstraction
5. add protocol config files and test fixtures

Deliverables:

- `NodeIdentity` model
- signed request verifier
- peer manifest schema
- initial network config loading

## Weeks 3-4: Multi-Node Replication

Goals:

- replicate every message to 2 nodes
- collect signed replica acknowledgements

Tasks:

1. add `/network/messages/ingest`
2. add `/network/messages/{messageId}/replicas`
3. add replica selection engine
4. add replica ack collection and quorum rules
5. extend integration tests to multi-node topologies

Deliverables:

- replica APIs
- placement engine v1
- local 3-node network test harness

## Weeks 5-6: Network Mailbox Reads

Goals:

- make inbox reads work from any attached node
- decouple inbox from local shared filesystem assumptions

Tasks:

1. add mailbox index API endpoints
2. add peer message fetch fallback
3. add gateway read layer for `MailboxId`
4. make inbox app consume gateway network endpoints
5. add mailbox index versioning

Deliverables:

- network-aware gateway APIs
- cross-node mailbox read flow
- inbox client reading from network data

## Weeks 7-8: Wallet Ownership And Privacy Tiers

Goals:

- connect mailbox ownership to Base wallet identity
- introduce privacy-tier state

Tasks:

1. add wallet challenge/verify auth flow
2. add `MailboxOwner` and `PrivacyTier` domain models
3. allow wallet-owned mailbox transfer logic
4. define pricing hooks for `Standard` vs `Private`
5. record policy commitments needed for privacy enforcement later

Deliverables:

- wallet auth endpoints
- mailbox ownership model
- privacy tier persistence

## Weeks 9-10: Base Contracts v1

Goals:

- deploy Basemail control-plane contracts to Base Sepolia

Tasks:

1. implement `BasemailToken`
2. implement `NodeRegistry`
3. implement `MailboxRegistry`
4. implement `RewardsLedger` stub
5. write deployment scripts and environment docs
6. add offchain contract client bindings

Deliverables:

- contract package
- Sepolia deployment artifacts
- contract integration library

## Weeks 11-12: Uptime Scores And Reward Epochs

Goals:

- add offchain scoring and onchain reward commitments

Tasks:

1. collect uptime observations
2. compute epoch performance scores
3. publish score root on Base
4. assign claimable `$Basemail` rewards
5. add slash/jail hooks for severe failures

Deliverables:

- uptime scorer
- epoch publisher
- reward accrual flow

## Success Criteria For v1

By the end of week 12, Basemail v1 should be able to:

1. register nodes on Base Sepolia
2. bind email addresses to global `MailboxId`s
3. accept a message on one node and store it on at least 2 nodes
4. retrieve mailbox data from any attached node
5. calculate offchain uptime scores and publish epoch roots
6. expose claimable `$Basemail` rewards in a stubbed but working flow

## Deferred Work

Not required for v1 completion:

- production encryption model
- true trustless storage proofs
- decentralized governance
- telemetry marketplace implementation
- mainnet launch
- premium service marketplace

## Recommended Repo Additions Next

After these docs, the next concrete repo changes should be:

1. `Basemail.Protocol` project for shared network DTOs and signatures
2. `Basemail.Contracts` folder for Solidity interfaces and deployments
3. `Basemail.Node` abstractions inside [SymposiaServer](/Users/jamal/Projects/Symposia/EmailProvider/SymposiaServer)
4. network integration tests with 3-node local orchestration
