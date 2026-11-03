#!/usr/bin/env bash
# End-to-end smoke for issue #135: S3 upload/download across a two-node cluster on one machine.
#   host A: LocalDev (anvil + chain gateway), storage on, blob gateway on
#   host B: Join host A's chain gateway, storage on
# Uses the real aws CLI against host A's blob gateway (no Traefik) and checks:
#   AC1/AC2 upload + byte-identical download, copies on both nodes
#   AC3     download with node B down
#   AC4     upload with node B down succeeds, then reaches node B within 60 s of its return
#   AC5     wrong secret -> 403
#   AC6     no storage nodes -> 503-class error within 30 s
#
# Usage: Network/scripts/s3-cluster-smoke.sh [work-dir]   (OFFSET_A / OFFSET_B env to change ports)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${1:-$(mktemp -d)}"; mkdir -p "$WORK"
A=${OFFSET_A:-30} B=${OFFSET_B:-31}
UI_A=$((5170 + A)) UI_B=$((5170 + B)) GW_A=$((8080 + A)) S3_A=$((9000 + A))
PASSWORD="smoke-test-password"
ACCESS_KEY="SYMSMOKETESTKEY00001" SECRET_KEY="smoke-secret-0123456789abcdefghijklmnopqrstuv"
PID_A="" PID_B=""
cleanup() { for p in $PID_A $PID_B; do kill -TERM "$p" 2>/dev/null || true; done; wait 2>/dev/null || true; }
trap cleanup EXIT
fail() { echo "FAIL: $*"; echo "logs in $WORK"; exit 1; }

dotnet build "$ROOT/Network/Symposia.NodeHost" -v q -nologo >/dev/null
BIN="$ROOT/Network/Symposia.NodeHost/bin/Debug/net9.0/Symposia.NodeHost"
start_a() { (cd "$ROOT" && exec "$BIN" --data-dir "$WORK/a" --port-offset $A) >>"$WORK/a.log" 2>&1 & PID_A=$!; }
start_b() { (cd "$ROOT" && exec "$BIN" --data-dir "$WORK/b" --port-offset $B) >>"$WORK/b.log" 2>&1 & PID_B=$!; }
wait_for() { for _ in $(seq 1 60); do curl -sf "$1" >/dev/null && return 0; sleep 0.5; done; fail "timed out waiting for $1"; }

configure() { # ui-port jar python-mutation [extra-json]
  [ -f "$2" ] || curl -sf -c "$2" -H 'content-type: application/json' -d "{\"password\":\"$PASSWORD\"}" http://127.0.0.1:$1/api/setup >/dev/null
  curl -sf -b "$2" http://127.0.0.1:$1/api/settings | python3 -c "
import json,sys; s=json.load(sys.stdin)['settings']; $3; print(json.dumps({'settings':s${4:+, $4}}))" \
  | curl -sf -b "$2" -X PUT -H 'content-type: application/json' -d @- http://127.0.0.1:$1/api/settings >/dev/null
}
gateway_status() { curl -sf -b "$WORK/a.jar" http://127.0.0.1:$UI_A/api/status | python3 -c "import json,sys; g=json.load(sys.stdin).get('blobGateway') or {}; print(g.get('healthyStorageNodes') or 0, g.get('underReplicated') or 0)"; }
blobs() { find "$WORK/$1/storage" -path '*blobs*' -type f 2>/dev/null | wc -l | tr -d ' '; }
has_blob() { find "$WORK/$1/storage" -path '*blobs*' -type f -name "*$2*" 2>/dev/null | grep -q .; }

export AWS_ACCESS_KEY_ID=$ACCESS_KEY AWS_SECRET_ACCESS_KEY=$SECRET_KEY AWS_DEFAULT_REGION=us-east-1 AWS_EC2_METADATA_DISABLED=true
S3="aws --endpoint-url http://127.0.0.1:$S3_A s3"

start_a; start_b
wait_for http://127.0.0.1:$UI_A/api/session; wait_for http://127.0.0.1:$UI_B/api/session
configure $UI_A "$WORK/a.jar" "s['network']['mode']='LocalDev'; s['storage']['enabled']=True; g=s['blobGateway']; g['enabled']=True; g['accessKeyId']='$ACCESS_KEY'" "'blobGatewaySecret':'$SECRET_KEY'"
configure $UI_B "$WORK/b.jar" "s['network']['mode']='Join'; s['network']['gatewayUrl']='http://127.0.0.1:$GW_A'; s['storage']['enabled']=True"

echo "waiting for the blob gateway to see both storage nodes..."
for _ in $(seq 1 60); do read -r healthy _ < <(gateway_status || echo "0 0"); [ "$healthy" -ge 2 ] && break; sleep 1; done
[ "$healthy" -ge 2 ] || fail "gateway sees $healthy storage node(s), expected 2"

head -c 1024 /dev/urandom >"$WORK/small.bin"; head -c $((5*1024*1024)) /dev/urandom >"$WORK/medium.bin"; : >"$WORK/empty.bin"
$S3 mb s3://proof >/dev/null
for f in small medium empty; do $S3 cp "$WORK/$f.bin" "s3://proof/dir one/ünï/$f.bin" >/dev/null; done
for f in small medium empty; do
  $S3 cp "s3://proof/dir one/ünï/$f.bin" "$WORK/$f.back" >/dev/null
  cmp -s "$WORK/$f.bin" "$WORK/$f.back" || fail "AC2: $f.bin differs after round trip"
done
echo "PASS AC1/AC2: uploads round-trip byte for byte"

sleep 2
[ "$(blobs a)" -ge 2 ] && [ "$(blobs a)" = "$(blobs b)" ] || fail "AC1: blob counts differ (a=$(blobs a) b=$(blobs b))"
echo "PASS AC1: both storage nodes hold every object ($(blobs a) blobs each)"

aws --endpoint-url http://127.0.0.1:$S3_A s3 ls s3://proof >/dev/null 2>&1 \
  && [ "$(AWS_SECRET_ACCESS_KEY=wrong aws --endpoint-url http://127.0.0.1:$S3_A s3 ls s3://proof 2>&1 | grep -c -E '403|AccessDenied|SignatureDoesNotMatch')" -ge 1 ] \
  || fail "AC5: wrong secret was not rejected with 403"
echo "PASS AC5: wrong secret rejected"

kill -TERM $PID_B; wait $PID_B 2>/dev/null || true; PID_B=""
$S3 cp "s3://proof/dir one/ünï/medium.bin" "$WORK/medium.back2" >/dev/null || fail "AC3: download failed with node B down"
cmp -s "$WORK/medium.bin" "$WORK/medium.back2" || fail "AC3: content differs"
echo "PASS AC3: download works with node B down"

head -c 2048 /dev/urandom >"$WORK/new.bin"
NEW_CID=$(shasum -a 256 "$WORK/new.bin" | cut -c1-64)
$S3 cp "$WORK/new.bin" s3://proof/new.bin >/dev/null || fail "AC4: upload failed with node B down"
has_blob b "$NEW_CID" && fail "AC4: node B unexpectedly has the new object while down"
echo "PASS AC4a: upload succeeds with node B down"

start_b; wait_for http://127.0.0.1:$UI_B/api/session
began=$(date +%s); repaired=""
for _ in $(seq 1 90); do has_blob b "$NEW_CID" && { repaired=$(( $(date +%s) - began )); break; }; sleep 1; done
[ -n "$repaired" ] || fail "AC4: object written while node B was down never reached it"
[ "$repaired" -le 60 ] || fail "AC4: repair took ${repaired}s (> 60s)"
echo "PASS AC4b: node B got the missing copy ${repaired}s after restarting"

kill -TERM $PID_B; wait $PID_B 2>/dev/null || true; PID_B=""
configure $UI_A "$WORK/a.jar" "s['storage']['enabled']=False"
began=$(date +%s)
if $S3 cp "$WORK/small.bin" s3://proof/should-fail.bin >"$WORK/ac6.out" 2>&1; then fail "AC6: upload succeeded with no storage nodes"; fi
took=$(( $(date +%s) - began ))
grep -q -E '503|ServiceUnavailable|SlowDown' "$WORK/ac6.out" || { cat "$WORK/ac6.out"; fail "AC6: expected a 503-class error"; }
[ "$took" -le 30 ] || fail "AC6: failure took ${took}s (> 30s)"
echo "PASS AC6: no storage nodes -> 503 in ${took}s"

echo "PASS: S3 cluster smoke"
