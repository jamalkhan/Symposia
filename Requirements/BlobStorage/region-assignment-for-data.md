# Region Assignment for Data

## Overview

Tenants may specify geographic placement constraints for their blobs and folders. The network respects these constraints when choosing which nodes store a given blob, enabling data residency guarantees, latency optimization, and geo-redundancy.

## Region Assignment Model

- Region assignment may be applied at three levels, in decreasing precedence:
  1. **Object level**: A specific blob carries its own region constraint.
  2. **Folder/prefix level**: A folder policy cascades to all blobs within it unless overridden at the object level.
  3. **Bucket/container level**: A bucket policy applies to all contents unless overridden at a lower level.
- A blob or folder may be assigned **zero, one, or more regions**:
  - **Zero regions**: No constraint. The placement algorithm selects any available, healthy nodes optimizing for performance and diversity. This is the default.
  - **One region**: All replicas must reside in nodes belonging to that region.
  - **Multiple regions**: Replicas must be distributed across the named regions, ensuring true geo-redundancy. At least one replica per named region is required.

## Placement Algorithm

When a blob is written, the gateway evaluates the following inputs to select target nodes:

1. **Region constraint** from the resolved policy (object → folder → bucket → none).
2. **Minimum replica count** (at least 2; see [Redundancy and Data Integrity](./redundancy-and-data-integrity.md)).
3. **Node health**: nodes must be online, verified, and have available capacity.
4. **Node performance scores**: prefer nodes with higher throughput, lower latency, and better uptime history.
5. **Geographic diversity**: within a region, prefer nodes that are not co-located (different AS numbers or physical facilities where identifiable).
6. **Current load**: balance writes across nodes rather than concentrating on the highest-scoring node.

If no nodes meeting the region constraint are available at write time, the write must fail with a clear error — the system must not silently violate a stated region constraint.

## Zero-Region Placement ("Best Effort")

- When no region is specified, the placement algorithm is free to optimize purely for performance and availability.
- It should still prefer geographic diversity across selected replicas to reduce correlated failure risk.
- Zero-region blobs may be automatically migrated to better-performing nodes as the network evolves.

## Data Residency and Compliance

- Region assignments are the mechanism by which tenants enforce data residency requirements (e.g., GDPR data localization).
- The network must not move a blob outside its assigned regions without explicit tenant authorization, even during automatic re-replication triggered by node failure.
- The on-chain blob metadata record includes the region assignment and the actual replica locations, providing an auditable proof of placement.

## Region Policy Inheritance Example

```
bucket: "my-bucket"          → region: [eu-west]
  folder: "archive/"         → region: []  (inherits eu-west from bucket)
  folder: "public/"          → region: []  (zero regions override, best effort globally)
    blob: "file.jpg"         → region: [us-east, eu-west]  (explicit multi-region override)
```
