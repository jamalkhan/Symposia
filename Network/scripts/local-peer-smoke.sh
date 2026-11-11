#!/usr/bin/env bash
# Local end-to-end smoke for issue #129: anvil + NodeRegistry + Blockchain Gateway
# + two storage nodes + one compute node. Each node registers its endpoint, discovers
# the others through the gateway, and handshakes. Prints each node's /v1/peers.
#
# Usage: Network/scripts/local-peer-smoke.sh [work-dir]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${1:-$(mktemp -d)}"
FOUNDRY="${FOUNDRY_BIN:-$HOME/.foundry/bin}"
RPC_PORT=18545 GW_PORT=18080
DEPLOYER_KEY=0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80  # anvil default account #0
PIDS=()
cleanup() { for p in "${PIDS[@]}"; do pkill -P "$p" 2>/dev/null || true; kill "$p" 2>/dev/null || true; done; pkill -f "$WORK/neon-stub" 2>/dev/null || true; }
trap cleanup EXIT

mkdir -p "$WORK"
echo "work dir: $WORK"
# #210: --prune-history, since cleanup() kills anvil and a killed anvil leaks history snapshots.
"$FOUNDRY/anvil" --port $RPC_PORT --prune-history 64 --silent & PIDS+=($!)
until curl -s -o /dev/null -X POST -H 'content-type: application/json' \
  --data '{"jsonrpc":"2.0","method":"eth_chainId","params":[],"id":1}' http://127.0.0.1:$RPC_PORT; do sleep 0.2; done

REGISTRY=$(cd "$ROOT/Blockchain/bootstrap-chain" && "$FOUNDRY/forge" script script/Deploy.s.sol:Deploy \
  --rpc-url http://127.0.0.1:$RPC_PORT --private-key $DEPLOYER_KEY --broadcast 2>&1 \
  | sed -n 's/.*NodeRegistry: *\(0x[0-9a-fA-F]\{40\}\).*/\1/p' | head -1)
EPOCH=$(cd "$ROOT/Blockchain/bootstrap-chain" && ls -t broadcast/Deploy.s.sol/31337/run-latest.json >/dev/null && \
  python3 -c "import json;print([t['contractAddress'] for t in json.load(open('broadcast/Deploy.s.sol/31337/run-latest.json'))['transactions'] if t['contractName']=='EpochRootRegistry'][0])")
echo "NodeRegistry: $REGISTRY"

dotnet build "$ROOT/Blockchain/Symposia.Blockchain.Gateway" -v q -nologo >/dev/null
dotnet build "$ROOT/BlobStorage/Symposia.BlobStorage.StorageNode" -v q -nologo >/dev/null
dotnet build "$ROOT/Database/Symposia.Database.ComputeNode" -v q -nologo >/dev/null

Gateway__RpcUrl=http://127.0.0.1:$RPC_PORT Gateway__NodeRegistryAddress=$REGISTRY \
Gateway__EpochRootRegistryAddress=$EPOCH Gateway__RelayerPrivateKey=$DEPLOYER_KEY \
  dotnet run --no-build --project "$ROOT/Blockchain/Symposia.Blockchain.Gateway" --urls http://127.0.0.1:$GW_PORT \
  >"$WORK/gateway.log" 2>&1 & PIDS+=($!)
for i in $(seq 1 120); do curl -sf http://127.0.0.1:$GW_PORT/v1/nodes >/dev/null && break; sleep 0.5; done
curl -sf http://127.0.0.1:$GW_PORT/v1/nodes >/dev/null || { echo "gateway did not start:"; tail -30 "$WORK/gateway.log"; exit 1; }

common() { # section port
  echo "$1__BlockchainGatewayUrl=http://127.0.0.1:$GW_PORT $1__NodeRegistryAddress=$REGISTRY $1__ChainId=31337 \
$1__AdvertisedEndpoint=http://127.0.0.1:$2 $1__DiscoveryIntervalSeconds=3"
}

for port in 15181 15182; do
  dir="$WORK/storage-$port"; mkdir -p "$dir"
  env $(common StorageNode $port) StorageNode__ListenPort=$port StorageNode__StorageRoot="$dir/blobs" \
    StorageNode__ManifestDbPath="$dir/manifest.db" StorageNode__NodeIdentityKeyPath="$dir/node.key" \
    dotnet run --no-build --project "$ROOT/BlobStorage/Symposia.BlobStorage.StorageNode" >"$dir.log" 2>&1 & PIDS+=($!)
done

# The compute daemon supervises Neon binaries; a stub that just stays alive stands in for them,
# since only the node's identity/peering surface is under test here.
dir="$WORK/compute-15183"; mkdir -p "$dir"
printf '#!/bin/sh\nexec sleep 100000\n' >"$WORK/neon-stub"; chmod +x "$WORK/neon-stub"
env $(common ComputeNode 15183) ComputeNode__DataRoot="$dir" ComputeNode__NodeIdentityKeyPath="$dir/node.key" \
  ComputeNode__PageserverExecutablePath="$WORK/neon-stub" ComputeNode__SafekeeperExecutablePath="$WORK/neon-stub" \
  ComputeNode__PostgresExecutablePath="$WORK/neon-stub" \
  dotnet run --no-build --project "$ROOT/Database/Symposia.Database.ComputeNode" --urls http://127.0.0.1:15183 \
  >"$dir.log" 2>&1 & PIDS+=($!)

echo "waiting for discovery..."
sleep "${SMOKE_WAIT_SECONDS:-25}"

echo "--- registry (gateway /v1/nodes)"; curl -s http://127.0.0.1:$GW_PORT/v1/nodes; echo
status=0
for port in 15181 15182 15183; do
  peers=$(curl -s http://127.0.0.1:$port/v1/peers)
  echo "--- node :$port"; echo "$peers"
  n=$(echo "$peers" | python3 -c "import json,sys;print(sum(p['status']=='Recognized' for p in json.load(sys.stdin)['peers']))")
  [ "$n" -eq 2 ] || { echo "FAIL: node :$port recognizes $n/2 peers"; status=1; }
done
[ $status -eq 0 ] && echo "PASS: every node recognizes the other two"
exit $status
