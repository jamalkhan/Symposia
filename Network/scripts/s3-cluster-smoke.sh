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
#   #140    an unsigned gRPC call straight to a storage node is refused (Unauthenticated)
#   #142    the storage gRPC port is TLS-only (cleartext h2c gets nothing); the cluster works over pinned TLS
#   #139    over HTTPS (local TLS proxy, as Traefik) the CLI's chunked uploads with CRC64NVME
#           trailing checksums round-trip byte for byte
#   #5      multipart: the default aws s3 cp of a 20 MB file (multipart above 8 MB) over HTTP and
#           HTTPS round-trips, lands on both nodes, and leaves no open uploads
#   #145    storage nodes advertise protocol version + features; the Network tab reports readiness
#           for a scheduled activation; once a storage feature no node has is activated on chain,
#           the gateway stops using the nodes
#
# Usage: Network/scripts/s3-cluster-smoke.sh [work-dir]   (OFFSET_A / OFFSET_B env to change ports)
# Mixed versions (#146): HOST_A_BIN / HOST_B_BIN point either host at another Node Host build (e.g.
# the previous release's), so the same checks run across N and N-1. Default: this checkout's build.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${1:-$(mktemp -d)}"; mkdir -p "$WORK"
A=${OFFSET_A:-30} B=${OFFSET_B:-31}
UI_A=$((5170 + A)) UI_B=$((5170 + B)) GW_A=$((8080 + A)) S3_A=$((9000 + A))
PASSWORD="smoke-test-password"
ACCESS_KEY="SYMSMOKETESTKEY00001" SECRET_KEY="smoke-secret-0123456789abcdefghijklmnopqrstuv"
PID_A="" PID_B="" PID_TLS=""
cleanup() { for p in $PID_A $PID_B $PID_TLS; do kill -TERM "$p" 2>/dev/null || true; done; wait 2>/dev/null || true; }
trap cleanup EXIT
fail() { echo "FAIL: $*"; echo "logs in $WORK"; exit 1; }

dotnet build "$ROOT/Network/Symposia.NodeHost" -v q -nologo >/dev/null
BIN="$ROOT/Network/Symposia.NodeHost/bin/Debug/net9.0/Symposia.NodeHost"
BIN_A="${HOST_A_BIN:-$BIN}" BIN_B="${HOST_B_BIN:-$BIN}"
echo "host A: $BIN_A"; echo "host B: $BIN_B"
CAST="${CAST:-$(command -v cast || echo "$HOME/.foundry/bin/cast")}"
ANVIL_KEY=0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80  # anvil default account #0 (LocalDev owner)
start_a() { (cd "$ROOT" && exec "$BIN_A" --data-dir "$WORK/a" --port-offset $A) >>"$WORK/a.log" 2>&1 & PID_A=$!; }
start_b() { (cd "$ROOT" && exec "$BIN_B" --data-dir "$WORK/b" --port-offset $B) >>"$WORK/b.log" 2>&1 & PID_B=$!; }
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

# #139: AWS CLI v2 only sends aws-chunked bodies with trailing checksums over HTTPS.
TLS_PORT=$((9400 + A))
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$WORK/tls.key" -out "$WORK/tls.pem" -days 1 -subj "/CN=localhost" \
  -addext "subjectAltName=DNS:localhost" >/dev/null 2>&1
python3 "$ROOT/Network/scripts/tls-proxy.py" $TLS_PORT $S3_A "$WORK/tls.pem" "$WORK/tls.key" & PID_TLS=$!
sleep 1
S3S="aws --endpoint-url https://localhost:$TLS_PORT --ca-bundle $WORK/tls.pem s3"
$S3S cp "$WORK/medium.bin" s3://proof/https-medium.bin >/dev/null || fail "#139: HTTPS chunked upload failed"
$S3S cp s3://proof/https-medium.bin "$WORK/https-medium.back" >/dev/null || fail "#139: HTTPS download failed"
cmp -s "$WORK/medium.bin" "$WORK/https-medium.back" || fail "#139: HTTPS round trip differs"
kill $PID_TLS 2>/dev/null; wait $PID_TLS 2>/dev/null || true; PID_TLS=""
echo "PASS #139: HTTPS chunked upload with trailing checksum round-trips"

sleep 2
[ "$(blobs a)" -ge 2 ] && [ "$(blobs a)" = "$(blobs b)" ] || fail "AC1: blob counts differ (a=$(blobs a) b=$(blobs b))"
echo "PASS AC1: both storage nodes hold every object ($(blobs a) blobs each)"

# #5: multipart upload through the stock CLI (8 MB parts), plain and over TLS.
head -c $((20*1024*1024)) /dev/urandom >"$WORK/big.bin"
$S3 cp "$WORK/big.bin" s3://proof/big-http.bin >/dev/null || fail "#5: multipart upload over HTTP failed"
$S3 cp s3://proof/big-http.bin "$WORK/big-http.back" >/dev/null && cmp -s "$WORK/big.bin" "$WORK/big-http.back" || fail "#5: HTTP multipart round trip differs"
python3 "$ROOT/Network/scripts/tls-proxy.py" $TLS_PORT $S3_A "$WORK/tls.pem" "$WORK/tls.key" & PID_TLS=$!
sleep 1
head -c $((20*1024*1024)) /dev/urandom >"$WORK/big2.bin"
$S3S cp "$WORK/big2.bin" s3://proof/big-https.bin >/dev/null || fail "#5: multipart upload over HTTPS failed"
$S3S cp s3://proof/big-https.bin "$WORK/big-https.back" >/dev/null && cmp -s "$WORK/big2.bin" "$WORK/big-https.back" || fail "#5: HTTPS multipart round trip differs"
kill $PID_TLS 2>/dev/null; wait $PID_TLS 2>/dev/null || true; PID_TLS=""
for f in big big2; do
  cid=$(shasum -a 256 "$WORK/$f.bin" | cut -c1-64)
  has_blob a "$cid" && has_blob b "$cid" || fail "#5: assembled object $f.bin is not on both nodes"
done
open=$(aws --endpoint-url http://127.0.0.1:$S3_A s3api list-multipart-uploads --bucket proof --query 'length(Uploads || `[]`)' --output text)
[ "$open" = "0" ] || fail "#5: $open multipart upload(s) left open"
etag=$(aws --endpoint-url http://127.0.0.1:$S3_A s3api head-object --bucket proof --key big-http.bin --query ETag --output text)
case "$etag" in *-3\"|*-3) ;; *) fail "#5: multipart object ETag '$etag' is not the S3 multipart form (md5-of-parts-3)";; esac
echo "PASS #5: 20 MB multipart uploads (HTTP and HTTPS) round-trip and are on both nodes"

aws --endpoint-url http://127.0.0.1:$S3_A s3 ls s3://proof >/dev/null 2>&1 \
  && [ "$(AWS_SECRET_ACCESS_KEY=wrong aws --endpoint-url http://127.0.0.1:$S3_A s3 ls s3://proof 2>&1 | grep -c -E '403|AccessDenied|SignatureDoesNotMatch')" -ge 1 ] \
  || fail "AC5: wrong secret was not rejected with 403"
echo "PASS AC5: wrong secret rejected"

# #142/#145: what host B serves depends on its build; read its services document.
SVC_B=$(curl -sf http://127.0.0.1:$((5180 + B))/v1/node/services)
feature() { python3 -c "import json,sys; print('yes' if '$1' in (json.loads(sys.argv[1]).get('features') or []) else 'no')" "$SVC_B"; }
storage_url() { python3 -c "import json,sys; s=json.loads(sys.argv[1]).get('storage') or {}; u=s.get('$1'); print(u or '')" "$SVC_B"; }
probe_status() { # url -> grpc-status of an unsigned, empty Probe
  printf '\x00\x00\x00\x00\x00' | curl -sk --max-time 5 ${2:-} -o /dev/null -D - -X POST \
    -H 'content-type: application/grpc' -H 'te: trailers' --data-binary @- \
    "$1/symposia.blobstorage.v1.StorageNode/Probe" | tr -d '\r' | awk -F': ' 'tolower($1)=="grpc-status"{print $2}'
}
TLS_URL=$(storage_url grpcTls); [ -n "$TLS_URL" ] || { u=$(storage_url grpc); case "$u" in https://*) TLS_URL=$u;; esac; }
PLAIN_URL=$(storage_url grpc); case "$PLAIN_URL" in https://*) PLAIN_URL="";; esac
if [ -n "$TLS_URL" ]; then
  # #142: the TLS port speaks TLS only.
  tls_port=${TLS_URL##*:}
  plain=$(printf '\x00\x00\x00\x00\x00' | curl -s --max-time 5 --http2-prior-knowledge -o /dev/null -w '%{http_code}' -X POST \
    -H 'content-type: application/grpc' --data-binary @- http://127.0.0.1:$tls_port/symposia.blobstorage.v1.StorageNode/Probe || true)
  [ "$plain" = "000" ] || fail "#142: cleartext h2c to the TLS gRPC port answered ($plain)"
  [ "$(probe_status "$TLS_URL" --http2)" = "16" ] || fail "#140: unsigned gRPC call over TLS was not refused with Unauthenticated"
  grep -rq "Added storage node .* at https://" "$WORK/a/logs" 2>/dev/null || grep -q "Added storage node .* at https://" "$WORK/a.log" \
    || fail "#145: the gateway did not connect to a storage node over TLS"
  echo "PASS #142/#140: TLS gRPC port refuses cleartext and unsigned calls; the gateway uses pinned TLS"
fi
if [ -n "$PLAIN_URL" ]; then
  # #145 overlap: the cleartext port (for the previous release's gateways) enforces caller auth too.
  [ "$(feature storage.grpc.plaintext)" = "yes" ] || [ "$(feature storage.grpc.tls)" = "no" ] || fail "#145: cleartext served but not advertised"
  [ "$(probe_status "$PLAIN_URL" --http2-prior-knowledge)" = "16" ] || fail "#140: unsigned gRPC call in cleartext was not refused"
  echo "PASS #145: cleartext overlap port serves gRPC and refuses unsigned calls"
fi

# #145: activations come from the chain; the Network tab reports readiness ahead of one.
NET=$(curl -sf http://127.0.0.1:$GW_A/v1/network)
PA=$(python3 -c "import json,sys; print(json.loads(sys.argv[1]).get('protocolActivationsAddress') or '')" "$NET")
RPC_A="http://127.0.0.1:$((8545 + A))"
if [ -n "$PA" ] && [ -x "$CAST" ]; then
  chain_now() { curl -sf http://127.0.0.1:$GW_A/v1/protocol | python3 -c "import json,sys; print(json.load(sys.stdin)['now'])"; }
  schedule() { "$CAST" send --rpc-url "$RPC_A" --private-key $ANVIL_KEY "$PA" 'schedule(string,uint64)' "$1" "$2" >/dev/null || fail "#145: could not schedule $1"; }
  schedule storage.grpc.tls $(( $(chain_now) + 86400 ))
  ready=$(curl -sf -b "$WORK/a.jar" http://127.0.0.1:$UI_A/api/network | python3 -c "
import json,sys; p=json.load(sys.stdin)['protocol']; a=[x for x in p['activations'] if x['name']=='storage.grpc.tls']
print(f\"{a[0]['ready']}/{a[0]['total']}\" if a else 'missing')")
  expected_ready=$([ "$(feature storage.grpc.tls)" = "yes" ] && echo "2/2" || echo "1/2")
  [ "$ready" = "$expected_ready" ] || fail "#145: Network tab readiness for storage.grpc.tls is '$ready', expected $expected_ready"
  echo "PASS #145: Network tab shows storage.grpc.tls scheduled with $ready storage nodes ready"
else
  echo "SKIP #145 activation checks: host A's network has no ProtocolActivations contract (older build) or cast is missing"
fi

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

# #145 cutover: activating a storage feature no node advertises makes the gateway stop using them
# (last, because activations are permanent on this chain).
if [ -n "$PA" ] && [ -x "$CAST" ]; then
  configure $UI_A "$WORK/a.jar" "s['storage']['enabled']=True"
  for _ in $(seq 1 60); do read -r healthy _ < <(gateway_status || echo "0 0"); [ "$healthy" -ge 1 ] && break; sleep 1; done
  [ "$healthy" -ge 1 ] || fail "#145: gateway did not pick storage node A back up"
  # The restarted storage role may be counted before it accepts writes; give it a moment.
  ok=""; for _ in $(seq 1 30); do $S3 cp "$WORK/small.bin" s3://proof/before-cutover.bin >/dev/null 2>&1 && { ok=1; break; }; sleep 1; done
  [ -n "$ok" ] || fail "#145: upload before cutover failed"
  schedule storage.smoke.unready $(( $(date +%s) + 5 ))  # anvil stamps the next block with wall-clock time; its latest block may be older
  refused=""
  for _ in $(seq 1 60); do
    grep -rqs "lacks required protocol feature(s) storage.smoke.unready" "$WORK/a" "$WORK/a.log" && { refused=1; break; }; sleep 1
  done
  [ -n "$refused" ] || fail "#145: gateway kept using a node after a feature it lacks was activated"
  if $S3 cp "$WORK/small.bin" s3://proof/after-cutover.bin >"$WORK/cutover.out" 2>&1; then fail "#145: upload succeeded on a refused node"; fi
  grep -q -E '503|ServiceUnavailable|SlowDown' "$WORK/cutover.out" || { cat "$WORK/cutover.out"; fail "#145: expected a 503-class error after cutover"; }
  echo "PASS #145: once storage.smoke.unready is activated the gateway drops nodes lacking it (uploads 503)"
fi

echo "PASS: S3 cluster smoke"
