# Blockchain — Phase 0 Minimal Bootstrap Chain

Implements issue #110: the smallest deployment of the protocol's own L3 (per
[`Requirements/Blockchain/chain-architecture.md`](../Requirements/Blockchain/chain-architecture.md))
sufficient for a Blob Storage node to complete cold-start step 1 — generate a
keypair (issue #109) and register on-chain — and to submit signed epoch
Merkle roots.

This is **not** a throwaway or parallel chain. It is the protocol's own L3
genesis, deployed early with a minimal contract set and the architecture's
already-specified Phase 1 centralized sequencer. The full buildout in issue
#57 (multi-region foundation nodes, staking, decentralized verifier quorum)
lands later as additional contracts and validators *on this same chain* —
there is no migration step, and no already-registered node is ever required
to re-register.

## Layout

- `bootstrap-chain/` — Foundry project.
  - `src/NodeRegistry.sol` — EIP-712-authenticated node identity registration. Idempotent.
  - `src/EpochRootRegistry.sol` — signed epoch Merkle root submission, gated on registration. Idempotent for identical resubmissions; rejects conflicting resubmissions for an already-submitted epoch.
  - `test/` — Foundry unit tests covering the full acceptance criteria (registration, rejection, forgery resistance, idempotency, reads).
  - `script/Deploy.s.sol` — deploys both contracts.
- `Symposia.Blockchain.Gateway/` — the foundation-operated Bootstrap Chain Gateway (relayer + read proxy). Freshly generated node keypairs have no L3 balance to pay gas, so nodes sign payloads locally and the Gateway relays them on-chain, paying gas itself (see the Arch pass on issue #110, "Gas bootstrapping"). The contracts — not the Gateway — are the authoritative rejector of unregistered or forged submissions.
- `Symposia.Blockchain.Gateway.Tests/` — end-to-end tests that boot a real local `anvil` chain, deploy the real contracts via `forge script`, and exercise the Gateway's HTTP surface against them.

## Running locally

Requires [Foundry](https://getfoundry.sh/) (`curl -L https://foundry.paradigm.xyz | bash && foundryup`) and the .NET 9 SDK.

```bash
# Contracts (first run: install deps, since lib/ is gitignored)
cd Blockchain/bootstrap-chain
forge install foundry-rs/forge-std --no-git
forge install OpenZeppelin/openzeppelin-contracts --no-git
forge test

# Gateway (against a local anvil + deployed contracts)
anvil &
forge script script/Deploy.s.sol:Deploy --rpc-url http://localhost:8545 \
  --private-key 0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80 --broadcast
# then set Gateway:NodeRegistryAddress / Gateway:EpochRootRegistryAddress / Gateway:RelayerPrivateKey
# in Symposia.Blockchain.Gateway/appsettings.json (or env vars) to the logged addresses, and:
dotnet run --project Symposia.Blockchain.Gateway

# Full test suite (spins anvil up/down itself)
dotnet test Symposia.Blockchain.Gateway.Tests
```

## Gateway API

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/v1/nodes/register` | Relay a signed registration payload |
| `GET` | `/v1/nodes/{address}` | Registration status |
| `POST` | `/v1/nodes/{address}/epoch-roots` | Relay a signed epoch root submission |
| `GET` | `/v1/nodes/{address}/epoch-roots/latest` | Latest submitted root + epoch |
| `GET` | `/v1/nodes/{address}/epoch-roots/{epoch}` | Root for a specific epoch |

Reads never require the Gateway — any caller can hit the contracts' view
functions directly over L3 RPC.

## Explicitly out of scope (tracked in #57)

Multi-region foundation node deployment, the full pre-staked allocation, the
12-month operational floor commitment, staking/governance contracts, and
growth to the full decentralized verifier quorum.
