# Security

## Overview

Security is a foundational requirement, not an afterthought. The system is designed so that **storage node operators have zero ability to read tenant data** — they store ciphertext only. The system is designed to meet HIPAA Technical Safeguard requirements (45 CFR §164.312) and to support tenants storing electronic Protected Health Information (ePHI).

---

## Authentication and Authorization

- All API requests must be authenticated using valid credentials (see [Multi-Tenancy and Credentials](../BlobStorage/multi-tenancy-and-credentials.md)).
- Authorization is enforced server-side on every request; clients cannot self-elevate privileges.
- Credential scope is validated at the point of access: a read credential is rejected on a write endpoint; a credential scoped to bucket A is rejected for bucket B.
- Unauthenticated access is only permitted for blobs explicitly marked public or for presigned URL requests with a valid, unexpired signature.
- **Unique user identification**: Every credential is bound to a specific user or application identity. Shared credentials are not permitted. Each actor in the system is individually identifiable in audit logs.
- **Automatic credential timeout**: API sessions and temporary credentials must carry a maximum lifetime. Long-lived credentials must be periodically re-validated. Inactive sessions are terminated after a configurable idle period (default: 15 minutes for interactive sessions, configurable for application credentials). This satisfies the HIPAA automatic logoff requirement (§164.312(a)(2)(iii)).
- **Minimum necessary access**: Credentials are scoped to the narrowest access required. The system must not issue credentials broader than the requestor's stated need. Tenants cannot request credentials broader than their own tenant scope.

---

## Encryption Architecture

### Core Principle: Node Operators Cannot Read Tenant Data

Storage nodes hold **only ciphertext**. Encryption and decryption occur at the gateway layer, using keys that are stored in a Key Management Service (KMS) that is architecturally separate from the storage node layer and inaccessible to node operators. A node operator with full access to their disk sees random encrypted bytes — never tenant data.

This is not optional. It is a hard architectural requirement.

### Encryption in Transit

- All data in transit is protected by **TLS 1.3** (TLS 1.2 minimum for legacy client compatibility) on all API endpoints, all gateway-to-node connections, and all inter-node communication.
- Certificate pinning is recommended for the node-to-node communication layer.
- Expired, self-signed, or unverifiable TLS certificates are rejected — no exceptions.

### No Backdoors — Ever

There are no backdoors in this system. Not for employees. Not for the platform operator. Not for governments, law enforcement, intelligence agencies, or any other authority. Not for anyone.

This is not a policy that can be overridden by an internal decision, a court order compliance process, or a future product requirement. It is a hard architectural guarantee enforced by the encryption model: the platform holds no keys, so there is nothing to hand over. A lawful demand for decrypted data cannot be fulfilled because the decrypted data does not exist anywhere in the platform's infrastructure.

Tenants hold their own keys via their external KMS. If a third party wants access to a tenant's data, they must direct that demand to the tenant, not to this platform.

This guarantee must be maintained in perpetuity. Any future feature, product addition, or architectural change that would give the platform — or any party other than the tenant — the ability to decrypt tenant data is prohibited. If a future internal key management service is ever implemented, it must be designed so that the platform operator cannot use it to unilaterally decrypt data without an explicit, auditable action by the tenant.

### Encryption at Rest — Client-Managed Keys (Required)

- Tenants are responsible for managing their own encryption keys using an external Key Management Service of their choice (e.g., AWS KMS, Azure Key Vault, HashiCorp Vault, or any compatible system).
- Before uploading data, tenants encrypt their blobs using keys sourced from their KMS. The platform receives and stores only ciphertext.
- Storage nodes receive, store, and return opaque encrypted blobs. They have no decryption capability and no access to any tenant's keys.
- The platform never receives, stores, touches, or has access to tenant encryption keys or plaintext data at any point.
- If a tenant loses access to their KMS or their encryption keys, their data is permanently and irrecoverably unreadable. The platform cannot assist with key recovery under any circumstances.

### Key Management (Tenant Responsibility)

- Key generation, rotation, revocation, and backup are entirely the tenant's responsibility, managed within their chosen external KMS.
- The platform provides documentation and integration guidance for common KMS providers but does not operate or access any tenant KMS.
- For HIPAA workloads, tenants should follow their KMS provider's HIPAA-eligible configuration guidance and maintain their own key rotation schedule (recommended: 90-day rotation for ePHI keys).
- Credential secrets (API keys, tokens) used to authenticate to the platform are stored as secure hashes server-side — never plaintext.
- Credential rotation is supported without service disruption.
- Revocation propagates to all nodes within 60 seconds. Revoked credentials cannot be re-issued with the same identifier.

---

## HIPAA Compliance

The system is designed to support tenants storing electronic Protected Health Information (ePHI) under the HIPAA Security Rule (45 CFR Part 164). The following requirements apply to all deployments where ePHI may be stored.

### Business Associate Agreements (BAAs)

- Storage node operators who physically hold encrypted tenant data are **Business Associates** under HIPAA, even though they cannot read the data.
- Every storage node operator must execute a Business Associate Agreement with the platform operator before their node is eligible to store blobs from HIPAA-designated tenant accounts.
- The BAA must cover: permitted uses of data, safeguard requirements, breach notification obligations, data return or destruction on contract termination, and subcontractor obligations.
- The platform operator must maintain a current register of all BAAs. Nodes without a current, valid BAA are excluded from placement of ePHI blobs.
- Tenants storing ePHI must also execute a BAA with the platform operator.

### ePHI Blob Designation

- Tenants may designate buckets or individual blobs as containing ePHI. This designation triggers additional controls:
  - The blob is only placed on nodes whose operators have signed a BAA.
  - The additional audit log retention period applies (see Audit Logging below).
  - The blob is excluded from any public access modes.
  - Key rotation is enforced on the platform-defined HIPAA schedule.

### Access Controls (§164.312(a))

- Unique user identification for every credential (covered above).
- Emergency access procedure: A documented, audited break-glass process exists for authorized personnel to access ePHI in genuine emergencies. Every use of the break-glass process is logged and reviewed.
- Automatic logoff: Covered under Authentication above.
- Encryption and decryption: Covered under Encryption Architecture above.

### Audit Controls (§164.312(b))

See Audit Logging section below. HIPAA requires hardware, software, and procedural mechanisms that record and examine activity in information systems containing ePHI.

### Integrity (§164.312(c))

- All blobs are integrity-verified via cryptographic hash on every read (see [Redundancy and Data Integrity](../BlobStorage/redundancy-and-data-integrity.md)).
- ePHI blobs are additionally subject to a periodic integrity audit: the system verifies that stored ciphertext has not been altered since ingest, on a schedule no less frequent than once per epoch.
- Any integrity failure on an ePHI blob triggers an immediate alert to the tenant and to the platform security team.

### Transmission Security (§164.312(e))

- Covered under Encryption in Transit above.
- End-to-end encryption ensures ePHI is protected not just in transit to the gateway, but from gateway to storage node.

### Breach Notification

- If a security incident results in a confirmed or suspected breach of unsecured ePHI, the platform must notify affected tenants **within 60 days of discovery**, per the HIPAA Breach Notification Rule (45 CFR §164.400).
- The platform must maintain an incident response plan that defines: breach identification, containment, impact assessment, notification, and post-incident review.
- Breaches affecting 500 or more individuals must also be reported to the Secretary of HHS and, in some cases, prominent media outlets, per HIPAA requirements.
- Because data is encrypted at rest with keys inaccessible to node operators, a node operator's disk being physically stolen or compromised does **not** constitute a breach of unsecured ePHI — the data is unintelligible without the KMS keys. This is the "Safe Harbor" provision under HIPAA.

### Risk Analysis and Management

- A documented risk analysis must be completed before the platform accepts ePHI. The analysis must identify reasonably anticipated threats to ePHI, the likelihood and impact of each, and mitigating controls.
- Risk analysis must be reviewed and updated: annually, after significant system changes, and after any security incident.
- This is not a one-time activity; it is an ongoing program.

### Data Disposal and Media Sanitization

- When a storage node is decommissioned or a node operator's BAA is terminated, all ePHI blobs must be migrated off the node before it is removed from service.
- Disk media that previously held encrypted ePHI must be cryptographically wiped (overwrite with random data) or physically destroyed before disposal. Certificate of destruction must be provided to the platform operator.
- Because blobs are encrypted, effective key deletion (destroying the tenant's DEK) renders all ciphertext on disk permanently unreadable. This satisfies media sanitization requirements even without physical destruction.

---

## Sybil and Collusion Resistance

- Node identity is bound to a cryptographic keypair; creating a new identity requires generating new keys. Sybil attacks (many identities from one physical node) are detectable because co-located nodes produce correlated latency measurements during region verification.
- The region verification protocol uses multiple independent verifiers, requiring an attacker to collude with a majority of the verifier set — made economically costly by the staking requirement.
- Verifier selection for each challenge is randomized and unpredictable to the node being challenged.

---

## Audit Logging

- Every API access event (reads, writes, deletes, list operations) is logged with: timestamp, tenant ID, credential ID, action, target resource, source IP, region, node ID, and outcome (success/failure).
- Every credential lifecycle event (creation, use, revocation, rotation) is logged.
- Every administrative action (tenant provisioning, capacity changes, node registration/deregistration) is logged.
- Every key management event (key creation, rotation, deletion, break-glass access) is logged in a separate, independently secured audit trail.
- Logs are **tamper-evident**: hash-chained so that deletion or modification of any log entry is detectable.
- On-chain events (blob deals, reward distributions, slashes) serve as an immutable audit trail for the economic layer.
- Tenants may query their own audit logs via the API.

**Retention:**
- Standard audit logs: minimum **1 year** hot, **3 years** total.
- ePHI-related audit logs: **6 years**, per HIPAA documentation retention requirements (§164.316(b)(2)).
- Logs must be stored in a location that is accessible for compliance review but isolated from the primary data path.

---

## DDoS and Abuse Protection

- Rate limiting is applied per credential and per IP on all API endpoints.
- Gateway nodes implement connection-level throttling and configurable block lists.
- Presigned URLs carry short default expiry times (default: 15 minutes; maximum: 7 days) to limit the blast radius of leaked URLs. See [Presigned URLs](../BlobStorage/presigned-urls.md) for full expiry rules, scope constraints, and HIPAA overrides.
- Large upload and download operations are subject to per-tenant bandwidth quotas (configurable).
- ePHI buckets cannot be configured for public access, presigned public URLs, or zero-auth access modes.

---

## Vulnerability Management

- The codebase undergoes a third-party security audit before the platform accepts ePHI from any tenant.
- Dependency versions are tracked and updated within 30 days of a disclosed vulnerability, or immediately for critical (CVSS ≥ 9.0) vulnerabilities.
- A responsible disclosure policy and dedicated security contact are established before the platform accepts any user data.
- Penetration testing is conducted annually and after major architectural changes.
