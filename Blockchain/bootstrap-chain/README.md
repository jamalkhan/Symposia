# bootstrap-chain

Foundry project for the protocol's L3 contracts. See [../README.md](../README.md) for how it fits
with the Bootstrap Chain Gateway.

## Setup

```shell
../../tools/install-contract-libs.sh   # forge-std + OpenZeppelin (and -upgradeable) at pinned versions
forge test
```

## Deploy scripts

| Script | Deploys | Used by |
|---|---|---|
| `script/Deploy.s.sol` | Phase 0: NodeRegistry, EpochRootRegistry, ProtocolActivations | Gateway tests, NodeHost LocalDev, `Network/scripts/local-peer-smoke.sh` |
| `script/DeployRewards.s.sol` | Node rewards: SYM token, staking registry, slashing, reward distributor, reliability, telemetry checkpoints, emission schedule | Local devnets; covered by `test/DeployRewards.t.sol` |

```shell
anvil &
KEY=0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80   # anvil account #0, dev only
forge script script/Deploy.s.sol:Deploy --rpc-url http://localhost:8545 --private-key $KEY --broadcast
forge script script/DeployRewards.s.sol:DeployRewards --rpc-url http://localhost:8545 --private-key $KEY --broadcast
```

`DeployRewards` logs every address and writes them to `deployments/rewards-<chainId>.json`
(override with `DEPLOYMENT_OUT`). It is **dev/test only** for now:

- Config is the owner-settable `MockProtocolConfig`; there is no governed config contract yet.
- Every role (timelock, metric/fault signer, verifier, challenger, telemetry committee) defaults
  to the deployer. Override with `GOVERNANCE_TIMELOCK`, `METRIC_SIGNER`, `FAULT_SIGNER`,
  `VERIFIER_ROLE`, `RELIABILITY_CHALLENGER`, `TELEMETRY_COMMITTEE` (comma-separated) and
  `TELEMETRY_THRESHOLD`.
- `GOVERNANCE_TIMELOCK` becomes the SYM upgrade authority, and the minter list (the emission
  contract, plus a deployer faucet of `DEV_MINT_ALLOWANCE`, default 10M SYM on chain 31337 only)
  is fixed at initialize. Getting either wrong on a real network means an upgrade.
- No fee treasury is set, so the post-year-8 floor stays unfunded until governance calls
  `NetworkRewardsEmission.setFeeTreasury` (#86).
