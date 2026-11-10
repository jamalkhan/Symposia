# Gateway read-only RPC proxy: limits

The Bootstrap Chain Gateway serves `POST /v1/rpc` so joined hosts can read the chain without the gateway exposing its upstream RPC URL (#172). It's public and unauthenticated, so it limits each client (#182).

## What's limited

- **Per client:** a token bucket keyed on the client's IP address, with IPv6 clients keyed by their /64. Each call costs 1 token. `eth_getLogs` costs `GetLogsCost` tokens. Refused calls still cost tokens.
- **Gateway-wide:** at most `MaxConcurrentRequests` calls are forwarded at once. Calls beyond that are refused, never queued. A call takes its slot only after its body has arrived. A body that hasn't arrived within `RpcProxy:TimeoutSeconds` is refused.

A limited call gets **HTTP 429** with a `Retry-After` header and a JSON-RPC error `-32005`. It never reaches the upstream. Joined hosts treat it as a failed read and retry on their next poll.

## Settings (`Gateway:RpcProxy:RateLimit`)

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `true` | `false` turns off both the bucket and the concurrency cap. |
| `TokensPerSecond` | `20` | Refill rate per client. |
| `BurstTokens` | `200` | Bucket size. Must be at least `GetLogsCost`. |
| `GetLogsCost` | `5` | |
| `MaxConcurrentRequests` | `64` | |
| `TrustedProxies` | `[]` | IPs or CIDRs of your reverse proxies. |

Invalid values stop the gateway at startup, with a message naming the key.

## Things to know

- **Behind a reverse proxy**, list it in `TrustedProxies`. Without it, every client is charged to the proxy's single bucket. With it, the gateway reads `X-Forwarded-For` from the right and stops at the first address that isn't a trusted proxy.
- **NAT:** clients behind one NAT share a bucket. So do LocalDev hosts on one machine, since they all use 127.0.0.1. The defaults leave plenty of headroom for normal joined-host traffic, but several hosts doing a cold Ledger scan at once will be slowed down. They still make progress.
- **Several gateways** each limit on their own.
- **Hosts on the previous release** handle a 429 less gracefully on the Ledger tab: one that lands while the ledger resumes is treated as a reorg, and the host rescans its recent rows. The data is a rebuildable cache, so nothing is lost for good.

## Metrics

Meter `Symposia.Blockchain.Gateway.RpcProxy`:

- `rpc_proxy.forwarded`
- `rpc_proxy.rejected`, tagged `reason` = `rate_limited`, `concurrency`, `method` or `invalid`
- `rpc_proxy.upstream_errors`, kept apart from rejections so a provider outage doesn't look like abuse

Rejections are logged at most once per client per minute.
