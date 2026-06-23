# Health Check and Readiness Endpoints

## Overview

Health check endpoints allow load balancers, container orchestrators (Kubernetes, ECS, Nomad), and monitoring systems to determine whether a gateway or storage node instance is functioning correctly. Without them, traffic may be routed to failed instances, deployments cannot safely roll out without downtime, and operators have no programmatic way to distinguish a healthy node from a hung one.

These endpoints are defined for both the **gateway** and the **storage node**, as each has distinct health semantics.

---

## Gateway Health Endpoints

### GET /health

**Purpose**: Liveness check. Answers: "Is this process alive and able to handle requests at all?"

Used by: load balancers, container restart policies.

**Always returns `200 OK`** as long as the gateway process is running and its HTTP server is accepting connections. If this endpoint returns anything other than 200, the process is considered dead and should be restarted.

Response:

```json
{
  "status": "ok",
  "version": "1.4.2",
  "uptime_seconds": 86400
}
```

This endpoint has no authentication requirement and imposes no rate limit. It must respond within 100ms or the caller treats it as failed.

---

### GET /ready

**Purpose**: Readiness check. Answers: "Is this gateway instance ready to handle tenant traffic?"

Used by: load balancers (to take an instance in or out of rotation), deployment orchestrators (to determine when a new instance is ready before removing the old one).

Returns `200 OK` when ready, `503 Service Unavailable` when not ready.

A gateway is **not ready** if any of the following are true:
- It has not yet loaded its initial node health cache from the gossip network.
- It has not yet connected to the metadata index.
- It is in a graceful shutdown drain window (actively shutting down; not accepting new requests).
- It cannot reach the minimum number of healthy storage nodes required to satisfy a write quorum.

Response when ready:

```json
{
  "status": "ready",
  "node_cache_size": 142,
  "metadata_index_connected": true,
  "min_eligible_nodes_available": true
}
```

Response when not ready (`503`):

```json
{
  "status": "not_ready",
  "reason": "node_cache_not_loaded",
  "retry_after_seconds": 5
}
```

This endpoint has no authentication requirement. It must respond within 500ms.

---

### GET /metrics

**Purpose**: Exposes operational metrics in **Prometheus text format** for scraping by monitoring systems (Prometheus, Grafana, Datadog agent, etc.).

Authentication: Requires a bearer token configured at the gateway level (not a tenant credential — this is an operator credential). Without the token, returns `401`.

Exposed metrics include:

```
# Request metrics
gateway_requests_total{method, status_code, operation} counter
gateway_request_duration_seconds{operation, quantile} summary
gateway_request_bytes_total{direction} counter   # direction: in|out

# Node routing metrics
gateway_node_selection_score{node_id} gauge
gateway_node_errors_total{node_id, error_type} counter
gateway_read_failovers_total counter

# Write metrics
gateway_write_quorum_reached_total counter
gateway_write_quorum_failed_total counter
gateway_write_fanout_duration_seconds summary

# Health
gateway_node_cache_size gauge
gateway_metadata_index_lag_seconds gauge
gateway_eligible_nodes_count gauge
```

---

## Storage Node Health Endpoints

### GET /health

**Purpose**: Liveness check for the storage node process.

Same semantics as the gateway: returns `200 OK` as long as the node process is alive. Used by the local system watchdog or container restart policy.

Response:

```json
{
  "status": "ok",
  "node_id": "node_abc123",
  "version": "1.4.2",
  "uptime_seconds": 604800
}
```

---

### GET /ready

**Purpose**: Readiness check. Answers: "Is this node ready to receive reads and writes from gateways?"

Returns `200 OK` when ready, `503` when not ready.

A storage node is **not ready** if:
- It has not yet joined the P2P gossip network (cannot receive replication traffic).
- Its on-chain registration is not yet confirmed (epoch not yet started; stake not yet confirmed).
- Region verification is pending or failed.
- The storage path is not writable (disk full, permissions error, filesystem error).
- The node is in a graceful shutdown drain window.

Response when ready:

```json
{
  "status": "ready",
  "node_id": "node_abc123",
  "region": "us-east",
  "tier": 2,
  "penalty_stage": 0,
  "storage_available_bytes": 1073741824000,
  "storage_used_bytes": 536870912000,
  "peer_count": 47,
  "region_verified": true
}
```

Response when not ready (`503`):

```json
{
  "status": "not_ready",
  "reason": "region_verification_pending",
  "retry_after_seconds": 30
}
```

---

### GET /metrics

**Purpose**: Prometheus-format metrics for node monitoring.

Authentication: Operator bearer token (same pattern as gateway).

Exposed metrics include:

```
# Storage
node_storage_available_bytes gauge
node_storage_used_bytes gauge
node_blob_count gauge

# I/O performance
node_read_throughput_bytes_per_second gauge
node_write_throughput_bytes_per_second gauge
node_iops_read gauge
node_iops_write gauge
node_io_queue_depth gauge

# Network
node_network_inbound_bytes_total counter
node_network_outbound_bytes_total counter
node_peer_latency_seconds{peer_id, quantile} summary

# Replication
node_replication_tasks_active gauge
node_replication_bytes_total counter
node_integrity_check_failures_total counter

# Health and penalties
node_penalty_stage gauge       # 0=none, 1=warning, 2=degraded, 3=slash, 4=hard-slash
node_uptime_pct gauge          # rolling epoch window
node_heartbeat_compliance_pct gauge

# SMART disk health (one gauge per SMART attribute)
node_smart_reallocated_sectors gauge
node_smart_pending_sectors gauge
node_smart_uncorrectable_errors gauge
node_smart_temperature_celsius gauge
node_smart_wear_level_pct gauge    # SSDs only
```

---

## Endpoint Summary

| Endpoint | Component | Auth Required | Purpose |
|---|---|---|---|
| `GET /health` | Gateway | No | Liveness — is the process alive? |
| `GET /ready` | Gateway | No | Readiness — safe to receive traffic? |
| `GET /metrics` | Gateway | Operator token | Prometheus metrics scrape |
| `GET /health` | Storage Node | No | Liveness — is the process alive? |
| `GET /ready` | Storage Node | No | Readiness — safe to receive writes? |
| `GET /metrics` | Storage Node | Operator token | Prometheus metrics scrape |

All health and readiness endpoints must respond within their stated timeout regardless of the state of external dependencies. A deadlocked or blocked process that cannot respond to `/health` within 100ms is treated as failed.
