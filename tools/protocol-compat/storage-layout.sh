#!/usr/bin/env bash
# Storage-layout snapshots of upgradeable contracts (issue #146): protocol/contracts/storage-layout/<C>.json
# These contracts use ERC-7201 namespaced storage, so the snapshot records each struct's fields and the
# storage-location constants (solc's linear layout is empty for them).
#   --write          regenerate the snapshots (commit them with the contract change)
#   --check <base>   fail if a snapshot is stale, or if existing state changed vs <base>'s snapshot
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SRC="$ROOT/Blockchain/bootstrap-chain/src"
OUT="$ROOT/protocol/contracts/storage-layout"
COMPAT="python3 -I $ROOT/tools/protocol-compat/compat.py"
mode="${1:?--write or --check <base>}"; base="${2:-}"
mkdir -p "$OUT"; tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

status=0; count=0
for file in $(grep -l "is .*GovernedUpgradeable" "$SRC"/*.sol | sort); do
  c=$(basename "$file" .sol); count=$((count + 1))
  $COMPAT solidity-layout "$file" >"$tmp/$c.json"
  if [ "$mode" = "--write" ]; then cp "$tmp/$c.json" "$OUT/$c.json"; continue; fi
  if ! cmp -s "$tmp/$c.json" "$OUT/$c.json"; then
    echo "::error::protocol/contracts/storage-layout/$c.json is stale: run tools/protocol-compat/storage-layout.sh --write and commit it"; status=1
  fi
  if git -C "$ROOT" show "$base:protocol/contracts/storage-layout/$c.json" >"$tmp/$c.base.json" 2>/dev/null; then
    $COMPAT storage-layout "$tmp/$c.base.json" "$tmp/$c.json" "$c" || status=1
  fi
done
[ "$mode" = "--write" ] && echo "wrote $count snapshot(s) to protocol/contracts/storage-layout"
exit $status
