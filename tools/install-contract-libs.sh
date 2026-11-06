#!/usr/bin/env bash
# Installs the Foundry libraries for Blockchain/bootstrap-chain at the versions the contracts are
# built and tested with (they are not committed). Used by CI; safe to run locally.
set -euo pipefail
cd "$(dirname "$0")/../Blockchain/bootstrap-chain"
FORGE="${FORGE:-$(command -v forge || echo "$HOME/.foundry/bin/forge")}"
[ -d lib/forge-std ] || "$FORGE" install foundry-rs/forge-std@v1.16.2 --no-git
[ -d lib/openzeppelin-contracts ] || "$FORGE" install OpenZeppelin/openzeppelin-contracts@v5.6.1 --no-git
[ -d lib/openzeppelin-contracts-upgradeable ] || "$FORGE" install OpenZeppelin/openzeppelin-contracts-upgradeable@v5.6.1 --no-git
