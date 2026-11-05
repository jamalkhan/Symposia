# Protocol Upgrades and Staggered Releases

## Overview

Symposia nodes are operated by many independent parties. They will not upgrade at the same moment, and the network cannot ask them to. Every release must interoperate with the release before it, and any change that would cut off nodes that haven't upgraded must take effect at a single, publicly announced moment that applies to the whole network.

This file defines what operators, gateways and tenants can rely on during an upgrade. How contributors meet these requirements, and how CI enforces them, is in [docs/engineering/protocol-compatibility.md](../../docs/engineering/protocol-compatibility.md). Tenant-facing API versioning is covered separately in [Platform/api-versioning-policy.md](../Platform/api-versioning-policy.md).

Issues: #145 (staggered releases), #146 (enforcement).

---

## Requirements

### R1 — Mixed versions interoperate
- A host at release N works with hosts at release N-1 in every direction: storage, gateway, compute and discovery.
- An operator can upgrade one host at a time with no coordinated restart.
- Tenants see no errors caused only by a mix of versions.

### R2 — Nodes say what they speak
Each node publishes a protocol version and the list of protocol features it serves, in its services document (`GET /v1/node/services`). Consumers tolerate fields and features they don't know.

### R3 — Overlap before removal
When a release replaces a behaviour, it serves the old and the new side by side. The old behaviour is removed no earlier than the release after the replacement becomes required.

### R4 — Network-wide activation
- A breaking requirement takes effect at a time recorded on chain, in the network's ProtocolActivations contract.
- It does not come from any gateway's configuration.
- Every gateway applies the same rule at the same time.

### R5 — Advance notice
- Activations on Testnet and Mainnet are scheduled at least the contract's notice window ahead (7 days or more).
- They can be postponed or cancelled before taking effect.
- They can never be moved earlier, and they are permanent once in effect.

### R6 — Readiness is visible
- Operators can see each node's version and features, every scheduled activation, how many storage nodes are ready for it, and which nodes will be refused.
- They see this in the Node Host's Network tab and in gateway logs.

### R7 — Safe failure
- If the schedule cannot be read, gateways keep enforcing every requirement they have seen in effect.
- A node whose identity proof (e.g. its TLS certificate proof) does not verify is refused. A gateway never downgrades to a weaker transport because a proof failed.

---

## Operator experience

| Phase | What the operator sees | What they do |
|---|---|---|
| Release N ships with a new feature (`offered`) | New option, default on, serving both old and new | Upgrade when convenient |
| Activation scheduled | Network tab: "Will be refused at <time>: lacks <feature>" on hosts that haven't upgraded; gateway warnings | Upgrade before the time |
| Activation in effect | Hosts that haven't upgraded are refused by gateways and flagged "Refused by gateways" | Upgrade to rejoin |
| Release after `required` | The legacy behaviour is removed | Nothing; already upgraded |

## First application: storage TLS (#142)

- Storage nodes serve gRPC over TLS (`storage.grpc.tls`) and, during the overlap, also in cleartext (`storage.grpc.plaintext`).
- Gateways prefer TLS and pin the node-key-vouched certificate.
- Once `storage.grpc.tls` is activated, gateways stop using cleartext-only nodes, and operators turn cleartext off.

## Out of scope (for now)

- Decentralized voting on activations. The foundation key schedules them until governance (#54).
- On-chain readiness signalling by nodes. The services document is the signal.
- Compatibility beyond one release back.
