# Developer Experience

## Overview

The S3 and Azure Blob compatible interfaces give the platform access to a large existing ecosystem of tools. But interface compatibility alone does not drive adoption — developers need clear documentation, a safe place to experiment, first-class SDKs for common languages, and a CLI for operations work. This file defines the developer-facing surface area required to make the platform approachable and productive.

---

## Sandbox and Testnet Environment

- A **public testnet** is available before mainnet launch. It uses test tokens with no real value, allowing developers and node runners to integrate, test, and experiment without financial risk.
- The testnet mirrors mainnet behavior exactly, including region verification, replication, epoch mechanics, and billing — but with accelerated epoch durations (e.g., 1 hour instead of 24 hours) so developers can observe the full lifecycle quickly.
- Testnet credentials are issued separately from mainnet credentials. A testnet account does not grant mainnet access.
- Testnet data is periodically purged (e.g., every 30 days). This is clearly communicated and not suitable for persistent data.
- A **local development mode** allows a single-node instance to run entirely on a developer's machine with no network connectivity required. This mode disables blockchain settlement, region verification, and multi-node replication — it is purely for API integration testing.
  - Local dev mode is distributed as a single binary or Docker image.
  - It exposes both the S3 and Azure interfaces on localhost.
  - Startup time target: under 5 seconds.

---

## SDKs

Official SDKs are provided for the most common languages used in backend and data engineering workloads. Because the platform exposes standard S3 and Azure interfaces, existing AWS and Azure SDKs work out of the box — but a first-party SDK adds platform-specific features (region assignment, performance tier selection, event subscriptions, usage queries) that the generic SDKs cannot express.

### Target Languages (initial)

| Language | Priority |
|---|---|
| .NET / C# | P0 — primary implementation language |
| TypeScript / Node.js | P0 — dominant in web backends and tooling |
| Python | P0 — dominant in data engineering and AI workloads |
| Go | P1 — common in infrastructure and CLI tooling |
| Rust | P2 — relevant for high-performance node integrations |
| Java / Kotlin | P2 — enterprise backend workloads |

### SDK Capabilities

Each SDK wraps the underlying S3/Azure interface but exposes the platform's native features directly:

- Blob upload and download with automatic multipart handling (the SDK handles chunking transparently for large files).
- Region assignment per upload request.
- Performance tier selection per bucket or upload.
- Presigned URL generation.
- Credential creation and rotation (where the tenant's auth allows it).
- Usage and cost queries.
- Event subscription management (see Blob Event Notifications requirements).
- Retry logic with exponential backoff built in.
- Streaming upload and download (no full-file buffering in memory).

---

## Command-Line Interface (CLI)

A first-party CLI covers the full operational surface area of the platform, suitable for use in scripts, CI/CD pipelines, and manual operations work.

### Core Commands

```
blob upload   <local-path> <bucket>/<key> [--region <region>] [--tier <tier>]
blob download <bucket>/<key> <local-path> [--range <start-end>]
blob delete   <bucket>/<key>
blob list     <bucket>[/<prefix>] [--recursive] [--format json|table]
blob info     <bucket>/<key>

bucket create <bucket> [--region <region>] [--tier <tier>]
bucket delete <bucket>
bucket list

credential create [--read-only] [--scope <bucket>] [--expires <duration>]
credential revoke <credential-id>
credential list

usage summary [--epoch <n>] [--format json|table]
usage history [--from <date>] [--to <date>]

node status   (for node runners — view own node's metrics and tier)
```

- The CLI reads credentials from environment variables or a local config file (`~/.blob/config`), following the same convention as the AWS CLI.
- Output defaults to human-readable table format; `--format json` enables machine-readable output for scripting.
- All commands support `--dry-run` to preview what would happen without executing.

---

## Web Console

A browser-based console provides a visual interface for tenants who prefer not to use the CLI or SDK directly.

### Minimum Feature Set

- **Bucket and blob browser**: Navigate buckets, folders, and blobs. Upload and download files. View metadata.
- **Usage dashboard**: Current epoch's storage and egress consumption, estimated cost, credit balance, and burn rate. (See Tenant Observability requirements for full detail.)
- **Credential manager**: Create, view, and revoke credentials. Display scopes and expiry dates.
- **Audit log viewer**: Searchable, filterable view of the tenant's audit events.
- **Event subscription manager**: Configure and test webhook subscriptions. (See Blob Event Notifications requirements.)
- **Billing history**: Itemized usage reports per epoch.
- **Account settings**: Contact info, alert thresholds, KMS integration config.

The web console authenticates using the same credential system as the API — there is no separate console-only auth mechanism.

---

## Documentation

### Required Before Public Launch

- **Quickstart guide**: From zero to first uploaded blob in under 10 minutes, using the CLI and each first-party SDK.
- **API reference**: Full S3 and Azure interface coverage, with platform-specific extensions documented alongside.
- **Region and tier guide**: How to assign regions and tiers, what the trade-offs are, examples.
- **Security and encryption guide**: How client-side encryption works, KMS integration examples for AWS KMS, Azure Key Vault, and HashiCorp Vault.
- **HIPAA compliance guide**: What the platform provides, what the tenant is responsible for, how to configure ePHI-designated storage.
- **Node runner guide**: Hardware requirements, setup, benchmarking, earnings estimation. (See Node Runner Onboarding requirements.)
- **Billing and retention guide**: How billing works, how to set retention policies, what happens on non-payment.
- **Migration guide**: How to migrate from S3 or Azure Blob Storage with no downtime, using the compatibility interfaces.

### Ongoing

- Changelog published with every API version increment.
- Status page showing current network health, active incidents, and epoch stats.
- Community forum or Discord for developer questions.

---

## Migration from S3 and Azure

Because the platform exposes S3 and Azure compatible interfaces, migration is achievable by updating endpoint URLs and credentials. However, active migration tooling reduces friction further:

- **Sync tool**: A CLI command (`blob sync s3://bucket destination-bucket`) that copies data from an existing S3 or Azure bucket into the platform, preserving key structure and metadata.
- **Parallel-write mode**: An SDK option that writes blobs to both the existing S3/Azure provider and this platform simultaneously, allowing live traffic to be validated against the new platform before cutting over.
- The sync tool respects the source bucket's folder structure and translates metadata fields where equivalents exist.
