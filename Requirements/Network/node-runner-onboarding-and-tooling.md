# Node Runner Onboarding and Tooling

## Overview

The network is only as strong as the operators running nodes. Onboarding a node must be accessible to technically capable individuals who are not blockchain engineers — a home lab operator with a NAS, a developer with a spare server, or a business adding storage capacity should all be able to get a node running, verified, and earning tokens within a few hours. This file defines the tooling, documentation, and process required to make that possible.

---

## Pre-Onboarding: Eligibility and Planning

Before running a node, an operator should be able to evaluate whether their hardware and connectivity meet the requirements and estimate their potential earnings.

### Hardware Assessment Tool

A publicly available web-based tool (no account required) allows prospective node runners to input their hardware specifications and receive:

- Their expected **performance tier** (Tier 1–4) based on stated specs.
- An **earnings estimate** showing expected token rewards per epoch at their tier, based on current network pricing and their offered capacity.
- A **ROI estimate**: how long to recover the cost of staking at current token prices and reward rates. Clearly marked as an estimate, not a guarantee.
- A checklist of any specs that fall below the minimum threshold and need improvement before joining.

### Minimum Requirements

Clearly published minimum hardware and connectivity requirements for each tier. A node that does not meet Tier 4 minimums is not eligible. Requirements include:

- Minimum disk capacity (offered to the network, not total disk size).
- Minimum outbound bandwidth (sustained, not peak).
- Minimum uptime commitment (operators who cannot meet 90% uptime should not join).
- Minimum stake required at the operator's desired capacity level.

---

## Onboarding Process

### Step 1 — Account Creation

- The node operator creates an account on the platform.
- Account creation requires: email address, a wallet address (to receive staking and rewards), and agreement to the Node Operator Terms of Service (which includes the Business Associate Agreement framework for handling tenant data as a sub-processor).
- KYC (Know Your Customer) verification may be required depending on jurisdiction and stake level. This is a legal requirement, not optional. The KYC provider and process are specified in the legal documentation.

### Step 2 — Node Software Installation

- The node software is distributed as:
  - A single binary for Linux (x86-64 and ARM64).
  - A Docker image for operators who prefer containerized deployment.
  - A guided installer script for common Linux distributions.
- The installer handles: dependency checks, directory structure creation, initial keypair generation, and configuration file setup.
- Target installation time: under 15 minutes for an experienced operator on a clean system.

### Step 3 — Configuration

The node is configured via a single YAML or TOML configuration file. Required settings:

```yaml
node:
  storage_path: /mnt/storage/blob-data
  offered_capacity_gb: 2000
  wallet_address: "0x..."

network:
  bootstrap_peers:
    - peer1.network.example
    - peer2.network.example

alerts:
  webhook_url: https://ops.example.com/alerts
  email: operator@example.com

tls:
  cert_path: /etc/ssl/node.crt
  key_path: /etc/ssl/node.key
```

- The storage path, offered capacity, and wallet address are the minimum required configuration.
- All other settings have sensible defaults.
- The configuration file is validated at startup; errors are reported with clear, actionable messages (not just "config invalid").

### Step 4 — Benchmark and Tier Classification

Before the node registers with the network, it runs a **local benchmark suite** to measure its actual performance:

- Sequential read and write speed.
- Random IOPS (4K, queue depth 1 and 32).
- Available RAM for caching.
- Outbound bandwidth (measured via a download from a benchmark server).
- Latency to a set of well-known peers.

The benchmark results are:
- Displayed to the operator with a preview of their expected tier classification.
- Signed by the node's keypair and submitted as part of the registration transaction.
- Used as the baseline for independent verification by verifier nodes.

Benchmark runtime: approximately 10 minutes.

### Step 5 — Staking

- The operator deposits the required stake from their wallet to the staking smart contract.
- The minimum stake is calculated based on the offered capacity and displayed during setup.
- The node software monitors stake balance and alerts the operator if it approaches the minimum threshold.

### Step 6 — Registration and Region Verification

- The node submits a registration transaction on-chain containing: node public key, region claim, benchmark results, and stake transaction reference.
- Verifier nodes initiate latency probes and bandwidth tests against the new node.
- The operator can monitor verification progress in real time via the node dashboard (see below).
- If verification passes, the node receives its first on-chain attestation and becomes eligible to receive new blob placements and earn rewards.
- Verification typically completes within 30 minutes of registration, depending on verifier availability.

If verification fails, the operator receives a detailed report explaining which measurements were inconsistent with the claimed region, allowing them to diagnose whether the issue is their network, their region claim, or a temporary measurement artifact. Failed nodes may re-attempt verification after a 24-hour cooldown.

---

## Node Dashboard

Every node has a local web-based dashboard (accessible at `http://localhost:8080` by default, configurable) that displays:

- **Status overview**: Online/offline, current tier, region, verification status.
- **Storage**: Used vs. offered capacity, number of blobs held, total data stored.
- **Performance metrics**: Real-time and historical graphs for IOPS, throughput, bandwidth, latency, and TTFB.
- **Earnings**: Current epoch score, rewards earned this epoch, total lifetime rewards, pending payout status.
- **Penalty status**: Current penalty stage (if any), specific trigger, and recommended action.
- **S.M.A.R.T. health**: Drive health indicators with clear warnings when thresholds are approached.
- **Replication activity**: Current active replication tasks (incoming and outgoing), bandwidth consumed by replication.
- **Logs**: Queryable node log with severity filtering.
- **Alerts**: History of all alerts sent, with delivery confirmation.

The dashboard is read-only from the local network. All configuration changes are made via the config file and a restart.

---

## Earnings Estimator (Ongoing)

After onboarding, the node dashboard includes a live earnings estimator that updates each epoch:

- Projected rewards for the current epoch based on current score.
- Projected rewards for the next epoch if current performance is maintained.
- Comparison to network average earnings for nodes at the same tier.
- Break-even analysis: at current earnings, how many epochs until the staked amount is recovered from rewards.

This gives operators ongoing visibility into whether their hardware investment is performing as expected and makes it easy to identify when hardware upgrades would significantly change their earnings tier.

---

## Capacity Management

- Operators scale **up** capacity by updating the `offered_capacity_gb` configuration value and restarting. The new capacity is announced to the network immediately.
- Operators scale **down** capacity by updating the configuration and initiating a controlled wind-down. The node software verifies that all blobs that would exceed the new capacity have been successfully re-replicated elsewhere before the reduction takes effect. The estimated time for this process is displayed before the operator confirms.
- Voluntary capacity reduction triggers the disincentive period (see [Node Runner Incentives and Penalties](./node-runner-incentives-and-penalties.md)). The dashboard shows the projected impact on earnings before the operator confirms the reduction.

---

## Maintenance and Updates

- **Software updates**: The node software checks for updates at startup and once per epoch. Security updates are flagged as mandatory. Non-security updates are recommended but not mandatory.
- **Graceful restart**: The node supports a graceful restart process that completes in-flight replication operations before stopping, minimizing disruption to the network.
- **Planned maintenance mode**: Operators can place their node in maintenance mode, which pauses new blob placements and notifies the network, without triggering penalty stages. Maintenance mode is limited to a defined maximum duration (e.g., 6 hours) before it is treated as unplanned downtime.
