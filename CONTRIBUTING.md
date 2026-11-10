# Contributing to Symposia

Thanks for helping build Symposia. This page covers how to get a change merged. Symposia is a network of independently run nodes, so changes to how nodes talk to each other follow extra rules, described in [Protocol compatibility](#protocol-compatibility) below.

## Workflow

- **Issues** are titled `Feature: …` or `Bug: …`. Larger work goes through spec → QA plan → architecture plan on the issue before implementation.
- **Branches** follow GitFlow: `master` → `dev` → `qa` → `stage`. `master` is production; there is no `production` branch. Work is promoted `dev` → `qa` → `stage` → `master`. Branch from `dev` as `feature_<issue>_<shortName>` or `bug_<issue>_<shortName>`, and open your PR into `dev`.
- **Pull requests** fill in the template, including the compatibility checklist when it applies.
- **Merging:** a maintainer approves, and every required check must pass. Changes to protocol areas need a code owner's review (`.github/CODEOWNERS`).

## Building and testing

You need the .NET 9 SDK, [Foundry](https://getfoundry.sh) (`forge`, `anvil`, `cast`) and Python 3.

```sh
tools/install-contract-libs.sh                 # Foundry libraries for the contracts (once)
dotnet test BlobStorage/Symposia.BlobStorage.slnx
dotnet test Network/Symposia.Network.slnx
dotnet test Database/Symposia.Database.slnx
dotnet test Identity/Symposia.Identity.slnx
dotnet test Blockchain/Symposia.Blockchain.Gateway.Tests   # boots anvil itself
(cd Blockchain/bootstrap-chain && forge test)
python3 -m unittest discover -s tools/protocol-compat
tools/protocol-compat/run.sh origin/dev        # protocol compatibility vs your base branch
Network/scripts/s3-cluster-smoke.sh            # end-to-end two-host cluster (needs the aws CLI)
```

CI runs the same commands (`.github/workflows/`). Please run them before pushing.

## Protocol compatibility

Operators upgrade on their own schedule, so the network always runs mixed versions. Any change to what nodes, gateways and contracts exchange must follow these rules:

1. **Support the current release and the one before it.** A host on your change must work with hosts on the previous release, in both directions.
2. **Never remove a behaviour in the same release that adds its replacement.** Serve both for at least one release.
3. **Breaking requirements switch on at an on-chain activation.** Don't add a local configuration switch that decides which peers are acceptable. Add a protocol feature in `protocol/features.json`, and let governance schedule its activation in the `ProtocolActivations` contract.
4. **Activations are announced in advance**, through the contract's notice window, including for security fixes.
5. **Ignore what you don't understand.** Unknown fields, enum values and feature names are never errors.

In practice:
- **JSON documents and protos** only gain optional fields.
- **Golden fixtures** in `protocol/fixtures/` are history: add new ones, never edit or delete old ones.
- **Contract storage** is append-only.
- **Database migrations** are expand/contract: add, dual-write, stop reading, then drop in a later release.

CI enforces whatever a machine can judge. Each failure names its rule and links the fix in [docs/engineering/protocol-compatibility.md](docs/engineering/protocol-compatibility.md#the-checks). Reviewers check the rest. A field that keeps its name but changes meaning, for example, is still a breaking change.

For the product requirements behind these rules, see [Requirements/Network/protocol-upgrades-and-staggered-releases.md](Requirements/Network/protocol-upgrades-and-staggered-releases.md).

## Security

Please don't open public issues for vulnerabilities. Contact the maintainer privately through GitHub (@jamalkhan) first.

## License

By contributing, you agree that your contributions are licensed under the repository's [LICENSE](LICENSE).
