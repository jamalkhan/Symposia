## What and why

<!-- What does this change, and which issue does it close? -->

Closes #

## Protocol compatibility

<!-- Required if this touches anything nodes, gateways or contracts exchange. See docs/engineering/protocol-compatibility.md -->

- [ ] No node-to-node, gateway or contract behaviour changes (skip the rest)
- [ ] A host on this change works with hosts on the previous release, in both directions
- [ ] New behaviour is a protocol feature in `protocol/features.json`, served **alongside** what it replaces
- [ ] Nothing is removed in the same release that adds its replacement
- [ ] No new local switch decides which peers are acceptable (that's `NetworkPolicy` + an on-chain activation)
- [ ] JSON documents and protos only gained optional fields; fixtures added for new shapes
- [ ] Contract storage only appended; migrations are expand/contract

**Activation plan** (if a feature will become required): <!-- feature name, which release marks it required, proposed activation time per network -->

**Semantic changes tools can't see** (same field, new meaning or timing): <!-- describe, or "none" -->

## Testing

<!-- Commands run locally; CONTRIBUTING.md lists the suites. -->
