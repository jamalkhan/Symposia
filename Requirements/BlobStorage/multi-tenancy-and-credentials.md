# Multi-Tenancy and Credentials

## Overview

The system is multi-tenant by design. Multiple independent users and organizations share a single cluster while remaining fully isolated from one another. Each tenant controls its own namespaces, data, and access credentials.

## Tenant Model

- A **tenant** is a top-level identity representing a user or organization.
- Tenants are fully isolated: one tenant cannot read, write, list, or otherwise access another tenant's data or credentials.
- Tenants map to one or more **accounts** that can own buckets/containers and blobs.
- Tenant provisioning (create, suspend, delete) is an administrative operation; self-registration may be offered via an optional portal.

## Credential Model

- Users and applications may generate credentials scoped to their tenant.
- Credentials come in two access levels:
  - **Read credentials**: `GetObject`, `HeadObject`, `ListObjects` only.
  - **Read-Write credentials**: full CRUD access including delete.
- Credentials may be scoped to a specific bucket, folder prefix, or individual object rather than the entire tenant namespace.
- Credentials may carry an optional expiration time, after which they are automatically invalid.
- Presigned URLs / temporary tokens must be derivable from a credential without calling a live service at retrieval time (capability-based or signed-token approach such as Macaroons or signed JWTs). See [Presigned URLs](./presigned-urls.md) for full signing mechanics, scope constraints, and expiry rules.
- Credentials may optionally be region-locked, restricting their validity to requests arriving from or targeting a specific region.

## Credential Lifecycle

- Any authorized user or application within a tenant may create new credentials.
- Credentials must be revocable at any time by the tenant. Revocation must propagate to all nodes within a bounded time window.
- A revocation list (or on-chain revocation for blockchain-integrated deployments) is maintained and checked on each authenticated request.
- Credential rotation is supported: issuing a replacement credential and revoking the old one.
- Audit events are recorded for credential creation, use, and revocation (see [Security](../Platform/security.md)).

## Access Control

- Bucket/container-level policies may restrict which credentials can access which buckets.
- ACLs may be applied at the object level for fine-grained access.
- A credential's stated scope is enforced server-side; a read credential presented to a write endpoint is rejected.
