#!/usr/bin/env bash
# End-to-end smoke for issue #133: two Node Hosts on one machine.
#   host A: LocalDev (anvil + gateway) with storage on
#   host B: Join host A's gateway, with storage + networking-only compute on
# Drives both through the config UI's API and checks each recognizes the other.
#
# Usage: Network/scripts/node-host-smoke.sh [work-dir]   (OFFSET_A / OFFSET_B env to change ports)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${1:-$(mktemp -d)}"; mkdir -p "$WORK"
A=${OFFSET_A:-30} B=${OFFSET_B:-31}
UI_A=$((5170 + A)) UI_B=$((5170 + B)) GW_A=$((8080 + A))
PASSWORD="smoke-test-password"
PIDS=()
cleanup() { for p in "${PIDS[@]}"; do kill -TERM "$p" 2>/dev/null || true; done; wait 2>/dev/null || true; }
trap cleanup EXIT

dotnet build "$ROOT/Network/Symposia.NodeHost" -v q -nologo >/dev/null
BIN="$ROOT/Network/Symposia.NodeHost/bin/Debug/net9.0/Symposia.NodeHost"
(cd "$ROOT" && exec "$BIN" --data-dir "$WORK/a" --port-offset $A) >"$WORK/a.log" 2>&1 & PIDS+=($!)
(cd "$ROOT" && exec "$BIN" --data-dir "$WORK/b" --port-offset $B) >"$WORK/b.log" 2>&1 & PIDS+=($!)

wait_for() { for _ in $(seq 1 60); do curl -sf "$1" >/dev/null && return 0; sleep 0.5; done; echo "timed out waiting for $1"; exit 1; }
wait_for http://127.0.0.1:$UI_A/api/session
wait_for http://127.0.0.1:$UI_B/api/session

configure() { # ui-port jar python-mutation
  curl -sf -c "$2" -H 'content-type: application/json' -d "{\"password\":\"$PASSWORD\"}" http://127.0.0.1:$1/api/setup >/dev/null
  curl -sf -b "$2" http://127.0.0.1:$1/api/settings | python3 -c "
import json,sys; s=json.load(sys.stdin)['settings']; $3; print(json.dumps({'settings':s}))" \
  | curl -sf -b "$2" -X PUT -H 'content-type: application/json' -d @- http://127.0.0.1:$1/api/settings >/dev/null
}

configure $UI_A "$WORK/a.jar" "s['network']['mode']='LocalDev'; s['storage']['enabled']=True"
configure $UI_B "$WORK/b.jar" "s['network']['mode']='Join'; s['network']['gatewayUrl']='http://127.0.0.1:$GW_A'; s['storage']['enabled']=True; s['compute']['enabled']=True; s['compute']['networkingOnly']=True"

echo "waiting for discovery..."
status=1
for _ in $(seq 1 30); do
  sleep 2
  a=$(curl -sf -b "$WORK/a.jar" http://127.0.0.1:$UI_A/api/status | python3 -c "import json,sys; print(sum(p['status']=='Recognized' for p in json.load(sys.stdin)['peers']))")
  b=$(curl -sf -b "$WORK/b.jar" http://127.0.0.1:$UI_B/api/status | python3 -c "import json,sys; print(sum(p['status']=='Recognized' for p in json.load(sys.stdin)['peers']))")
  if [ "$a" -ge 1 ] && [ "$b" -ge 1 ]; then status=0; break; fi
done

for side in a b; do
  ui=$UI_A; [ $side = b ] && ui=$UI_B
  echo "--- host $side (http://127.0.0.1:$ui)"
  curl -sf -b "$WORK/$side.jar" http://127.0.0.1:$ui/api/status | python3 -c "
import json,sys; s=json.load(sys.stdin)
print('  node', s['node'])
for c in s['components']: print('  %-8s %-9s %s' % (c['name'], c['status'], c['reason'] or ''))
for p in s['peers']: print('  peer', p['endpoint'], p['status'], p['reason'])"
done

# Network tab: host A sees both nodes, with B's health fetched from B (storage + compute lit, not grey).
if [ $status -eq 0 ]; then
  curl -sf -b "$WORK/a.jar" http://127.0.0.1:$UI_A/api/network | python3 -c "
import json,sys; s=json.load(sys.stdin)
for n in s['nodes']: print('  network', n['host'], n['node'], 'storage', n['storage']['level'], 'compute', n['compute']['level'], '(self)' if n['self'] else '')
other=[n for n in s['nodes'] if not n['self']]
ok = len(s['nodes'])==2 and other and other[0]['storage']['level'] not in ('Off','Unknown') and other[0]['compute']['level'] not in ('Off','Unknown')
sys.exit(0 if ok else 1)" || { echo "FAIL: network tab did not report peer health"; status=1; }
fi

[ $status -eq 0 ] && echo "PASS: both hosts recognize each other" || echo "FAIL: hosts did not recognize each other (logs in $WORK)"
exit $status
