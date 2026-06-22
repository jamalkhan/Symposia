# GDPR and Data Privacy Regulations

## Overview

The platform operates globally and stores data for tenants in multiple jurisdictions. The General Data Protection Regulation (GDPR) is the most comprehensive data privacy law currently in force and sets requirements that affect the platform's architecture, data handling practices, and contractual obligations. This file documents GDPR compliance requirements and extends them to other major privacy frameworks where applicable.

GDPR compliance in this context has a useful property: because the platform never holds encryption keys and never sees plaintext tenant data, many of the hardest GDPR obligations are simplified. The platform is a data processor; the tenant is the data controller. The tenant's users are the data subjects.

---

## Role Definitions

| Party | GDPR Role | Responsibility |
|---|---|---|
| **Tenant** | Data Controller | Determines the purpose and means of processing personal data. Responsible for their users' rights and consent. |
| **Platform** | Data Processor | Processes data on behalf of the controller per the DPA. Does not determine purpose or means. |
| **Node Operators** | Sub-Processor | Process encrypted data under contract with the platform. Cannot read data. |
| **Data Subjects** | Individuals | Whose personal data may be stored by the tenant's application. |

---

## Data Processing Agreement (DPA)

- Every tenant storing personal data of EU residents must execute a **Data Processing Agreement** with the platform operator before doing so. This is the GDPR equivalent of HIPAA's Business Associate Agreement.
- The DPA must specify: subject matter and duration of processing, nature and purpose, type of personal data, categories of data subjects, and the obligations and rights of the controller.
- The platform's standard DPA is publicly available and must be reviewed by legal counsel before being offered to tenants.
- Sub-processor agreements (with node operators) must offer equivalent protections to the main DPA. Node operators operating in the EU must be listed as sub-processors in the DPA.

---

## Right to Erasure (Article 17 — "Right to be Forgotten")

A data subject may request that a controller delete their personal data. When a tenant receives such a request, they must be able to delete the specific data from this platform.

### How Erasure Works

- The tenant deletes the relevant blob(s) via the standard delete API.
- Because all data is encrypted with tenant-managed keys, the tenant may alternatively **delete the encryption key** for those blobs, rendering all ciphertext permanently unreadable without performing a delete operation on each individual blob. This is called **crypto-erasure** and satisfies the GDPR right to erasure.
- Crypto-erasure is particularly useful when personal data is distributed across many blobs or when the tenant cannot enumerate exactly which blobs contain a specific person's data.
- Hard-deleted blobs are purged from all storage nodes within the standard deletion propagation window (target: 72 hours from all replicas, consistent with other operational timelines).
- **Backup copies**: GDPR allows that data in backup systems may persist for the duration of the backup retention cycle, provided it is not actively processed and is deleted at the next backup cycle. The platform's replica system is not a backup — deletion propagates to all replicas.

### Tenant Responsibility

- The platform provides the deletion and crypto-erasure tools. The tenant is responsible for identifying which blobs contain a given person's data, as the platform cannot read encrypted content to help identify it.
- Tenants storing personal data are advised to maintain a data map (which blobs contain which users' data) in their own systems to enable efficient erasure responses.

---

## Right to Data Portability (Article 20)

Data subjects have the right to receive their personal data in a portable format.

- The platform's standard download APIs (S3/Azure compatible) satisfy this requirement from the platform's perspective — tenants can retrieve all their data at any time.
- Tenants are responsible for providing their end users with a portable export of data stored on this platform that pertains to them.
- The platform does not mediate between data subjects and tenants directly; requests go to the tenant (the controller), not the platform.

---

## Data Residency and Cross-Border Transfers

GDPR restricts transfers of personal data outside the EU/EEA unless adequate protections are in place.

- The region assignment system (see Region Assignment requirements) is the mechanism by which tenants enforce data residency. A tenant can pin EU personal data to `eu-*` regions, ensuring it is never written to nodes outside the EU.
- The platform must be able to guarantee that region constraints are honored — blobs assigned to EU regions are never replicated to non-EU nodes, not even as overflow copies (see Redundancy requirements for the overflow copy rules; GDPR tenants may need to configure their region assignments to disable global overflow).
- For cross-border transfers where residency is not enforced, the platform must maintain Standard Contractual Clauses (SCCs) with non-EU sub-processors (node operators) as the transfer mechanism.
- The platform publishes a list of all regions, their geographic location, and the legal jurisdiction applicable to nodes in each region.

---

## Privacy by Design and Default (Article 25)

- **No unnecessary data collection**: The platform collects only the data needed to operate the service. Access logs capture source IPs for security purposes; these are subject to the platform's own privacy policy and the applicable retention period.
- **Default to privacy**: New buckets default to private (no public access). Tenants must explicitly enable public access; there is no default-public mode.
- **Encryption by default**: All tenant data is encrypted with tenant-managed keys. There is no unencrypted storage mode.
- **Minimum necessary access**: Credentials are scoped as narrowly as possible. The platform does not issue broader credentials than requested.

---

## Data Breach Notification (Article 33 / 34)

- If the platform experiences a security incident that results in a breach of personal data, the platform must notify the tenant (as data controller) **within 72 hours** of becoming aware of the breach.
- The notification must include: the nature of the breach, categories and approximate number of data subjects affected, likely consequences, measures taken or proposed to address the breach.
- The tenant (as controller) is then responsible for notifying the relevant supervisory authority (within 72 hours of their own awareness) and potentially the affected data subjects.
- Because data is encrypted with tenant-managed keys, a physical breach of storage media (e.g., a node operator's disk being stolen) does not constitute a breach of personal data — the ciphertext is unreadable without the tenant's keys.

---

## Records of Processing Activities (Article 30)

- The platform maintains records of its processing activities as a processor, as required by Article 30(2).
- These records include: the name and contact details of the processor and controller, categories of processing performed, cross-border transfers, and a general description of technical and organisational security measures.
- These records are available to supervisory authorities on request.

---

## Other Applicable Frameworks

While GDPR is the primary framework addressed here, the following also apply and should be reviewed with legal counsel:

| Framework | Jurisdiction | Key Requirement |
|---|---|---|
| **CCPA / CPRA** | California, USA | Right to deletion, right to know, opt-out of sale. Tenants with California users are responsible; platform provides deletion tooling. |
| **PIPL** | China | Strict rules on cross-border data transfer out of China. A `cn-*` region with no cross-border overflow is required to serve Chinese tenants. |
| **PDPA** | Various (Thailand, Singapore, etc.) | Similar to GDPR; region residency controls satisfy most requirements. |
| **UK GDPR** | United Kingdom | Post-Brexit equivalent of EU GDPR. Same technical requirements. |
| **HIPAA** | USA | Covered in Security requirements. |

The platform does not certify compliance with these frameworks on behalf of tenants — compliance is a shared responsibility. The platform provides the technical controls (encryption, region residency, deletion, audit logs); tenants are responsible for their own obligations as data controllers.
