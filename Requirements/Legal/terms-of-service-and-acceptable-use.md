# Terms of Service and Acceptable Use Policy Requirements

## Overview

The Terms of Service (ToS) and Acceptable Use Policy (AUP) are the foundational legal agreements between the platform operator and its two user classes: tenants (who store and retrieve data) and node operators (who provide storage capacity). These documents must be drafted by qualified legal counsel, reviewed for enforceability in applicable jurisdictions, and updated when the platform materially changes. This requirements document defines what the ToS and AUP must cover — it is a specification for legal drafting, not the legal documents themselves.

---

## Tenant Terms of Service — Required Coverage

### Parties and Acceptance

- Clearly identify the contracting parties: the platform operating entity (legal name and jurisdiction) and the tenant.
- Specify how acceptance occurs (account creation, first upload, API key generation).
- Define the effective date of the agreement for each tenant.
- Specify the minimum age (e.g., 18 years or legal adult in the tenant's jurisdiction) required to enter the agreement.

### Service Description

- Define the service: decentralized blob storage accessible via S3-compatible and Azure-compatible interfaces, on a decentralized network of independently operated storage nodes.
- Explicitly disclaim that the platform is a **data processor**, not a data controller (see [GDPR and Data Privacy Regulations](./gdpr-and-data-privacy-regulations.md)).
- State that the platform has no access to tenant data and cannot assist with key recovery under any circumstances.

### Data Ownership and License

- State unambiguously that **tenants own their data**. The platform claims no ownership, license, or right to use tenant content for any purpose.
- The platform is granted only the operational rights necessary to provide the service: storing, replicating, and retrieving ciphertext blobs at the tenant's direction.
- The platform does not analyze, monetize, or share tenant data with third parties for any purpose.

### Billing and Payment

- Reference the billing model: prepaid credits, per-byte-per-epoch storage, per-GB egress (see [Data Retention and Billing](../Platform/retention-and-billing.md)).
- Define the non-payment schedule (grace period → soft suspension → soft delete → hard delete) and the exact timelines.
- State that credits are non-refundable once the stablecoin-to-token swap has executed.
- Specify that the platform's credit balance estimates are non-binding; the metered usage record on-chain is authoritative for billing purposes.

### Acceptable Use Policy (Incorporated by Reference)

The ToS must incorporate the AUP by reference and state that violation of the AUP is grounds for immediate account suspension.

### Suspension and Termination

- The platform may suspend an account immediately and without notice for: confirmed violations of the AUP, non-payment beyond the defined schedule, law enforcement holds (metadata-only), or confirmed fraudulent account creation.
- The platform may terminate an account (with 30 days notice and data retrieval window) for: sustained non-payment beyond the hard delete threshold, repeated AUP violations, or sanctions compliance requirements.
- Tenants may terminate their account at any time by deleting all data and closing their account. No early termination fee.
- Upon termination, any remaining credit balance (in native token) is forfeited if the account was terminated for cause, or converted to a final credit statement if terminated voluntarily (tenant may instruct the platform to return the equivalent stablecoin value; such returns are at the platform's discretion and subject to applicable minimums).

### SLA and Liability

- Reference the SLA (see [SLA and Availability Guarantees](../Platform/sla-and-availability-guarantees.md)).
- Define the service credit regime as the **exclusive remedy** for SLA breaches.
- Cap the platform's aggregate liability to the total amount paid by the tenant in the 12 months preceding the claim.
- **No liability for consequential damages**: The platform is not liable for lost profits, lost data (beyond service credits), business interruption, or any other indirect or consequential damages arising from service failures.
- Explicitly disclaim any warranty beyond what is stated in the SLA (no implied warranties of merchantability, fitness for a particular purpose, or uninterrupted service).

### Dispute Resolution

- Reference the dispute resolution process (see [Dispute Resolution](./dispute-resolution.md)).
- Specify the governing law and jurisdiction for the agreement (to be determined by legal counsel, typically the jurisdiction of the operating entity).
- **Binding arbitration clause**: Disputes that cannot be resolved through the dispute resolution process are subject to binding arbitration under a specified ruleset (e.g., JAMS or AAA). Legal counsel must confirm enforceability in applicable jurisdictions before finalizing.
- **Class action waiver**: Tenants waive the right to participate in class action lawsuits or class arbitrations. Legal counsel must review this clause for jurisdictional enforceability.
- **Exception**: Claims that may be brought in small claims court are not subject to arbitration.

### DMCA Agent

- The platform must designate a registered DMCA agent with the U.S. Copyright Office (required under 17 U.S.C. § 512).
- The agent's name and contact information must be published in the ToS and on the platform's website.
- The platform's response to DMCA takedown notices is governed by the Content Moderation policy (see [Content Moderation and Legal Policy](./content-moderation-and-legal-policy.md)).
- Because data is encrypted with tenant-managed keys, the platform cannot identify or remove specific infringing content; the DMCA response consists of disabling access to the identified tenant account or specific access credentials as appropriate.

### Privacy Policy Reference

- The ToS must reference a separate Privacy Policy that covers how the platform collects, uses, and protects personal data about tenants themselves (not tenant-stored data) — account information, billing details, usage metadata, IP addresses, support communications.
- The Privacy Policy must comply with GDPR, CCPA, and other applicable frameworks as detailed in [GDPR and Data Privacy Regulations](./gdpr-and-data-privacy-regulations.md).

### Changes to Terms

- The platform may update the ToS with 30 days notice for material changes and 7 days notice for non-material changes.
- Continued use after the effective date of updated terms constitutes acceptance.
- Tenants who do not accept material changes may terminate their account without penalty within the notice period.

---

## Node Operator Terms of Service — Required Coverage

Node operators require a separate agreement from tenants because their obligations and relationship with the platform are fundamentally different. The node operator ToS must address:

### Operator Role and Obligations

- Define the operator's role as an independent contractor providing storage services, not an employee or agent of the platform.
- State that the operator is a **sub-processor** of tenant data under GDPR (storing encrypted blobs on behalf of tenants via the platform) and that the operator may not access, use, or share any ciphertext they store.
- Require the operator to execute a **Business Associate Agreement** (BAA) as a condition of accepting any HIPAA-designated blobs (see [Security](../Platform/security.md)).
- Require the operator to comply with all applicable laws in the jurisdiction(s) where their node operates, including export controls, data protection laws, and any local regulations on operating data storage services.

### Hardware and Infrastructure Requirements

- The operator must maintain hardware that meets the declared performance tier at all times.
- The operator must ensure that their node is reachable at the registered public IP/port during declared uptime windows.
- The operator is responsible for physical and logical security of their hardware.

### Staking and Slashing

- The operator acknowledges that staked tokens are subject to slashing per the rules in [Node Runner Incentives and Penalties](../Network/node-runner-incentives-and-penalties.md) and that slashed tokens are permanently removed.
- The operator acknowledges that they have reviewed and understand the progressive penalty stages and their triggers.
- The operator agrees that on-chain slash events are deterministic and self-executing and that the platform has no authority to reverse them (except per the slash dispute process in [Dispute Resolution](./dispute-resolution.md)).

### Capacity Commitment and Wind-Down

- The operator commits to providing a minimum of 30 days notice before permanently decommissioning a node.
- The operator must participate in an orderly data migration during decommissioning (keeping the node online until the network confirms all blobs have been re-replicated).
- Voluntary capacity reduction rules and disincentive periods are as defined in [Node Runner Incentives and Penalties](../Network/node-runner-incentives-and-penalties.md).

### Prohibited Activities

- The operator may not intentionally delay, corrupt, or discard blobs.
- The operator may not inspect, copy, or attempt to decrypt any tenant data.
- The operator may not forge performance metrics, region verification measurements, or proof-of-possession responses.
- The operator may not run multiple node identities from the same physical hardware to artificially inflate staking rewards (Sybil attack).
- Violation of any prohibited activity triggers immediate Stage 4 slashing and permanent ban from the network.

---

## Acceptable Use Policy — Required Coverage

The AUP applies to tenants and defines what may and may not be stored on the platform.

### Permitted Uses

- Storage of any lawful data, including personal files, application data, database backups, media files, software artifacts, and business records.
- Storage of personal health information (PHI/ePHI) subject to the HIPAA compliance requirements and BAA execution.
- Storage of data subject to export controls, provided the tenant is solely responsible for compliance with those controls.

### Prohibited Uses

The platform must not be used to store, transmit, or distribute:

- **Child sexual abuse material (CSAM)**: Zero tolerance. Any confirmed detection triggers immediate account termination and reporting to the National Center for Missing & Exploited Children (NCMEC) and applicable law enforcement, as required by law. (See [Content Moderation and Legal Policy](./content-moderation-and-legal-policy.md) for detection mechanisms.)
- **Content that facilitates terrorism or mass violence**: Material produced by designated terrorist organizations, recruitment content, or operational planning material for acts of mass violence.
- **Malware and cyberweapons**: Viruses, ransomware, exploit kits, or tools designed primarily to cause harm to third parties.
- **Spam infrastructure**: Email lists, phishing kits, or credential harvesting tools.
- **Data obtained illegally**: Stolen personal data, unauthorized database dumps, scraped data obtained in violation of the source platform's ToS.
- **Content violating third-party intellectual property**: Material that infringes copyright, trademark, or other intellectual property rights of third parties.

### Platform Limitations

The AUP must clearly state that the platform:
- Cannot inspect tenant data (by architectural design — all data is encrypted with tenant-managed keys).
- Cannot proactively monitor content for prohibited material.
- Relies on third-party reports, hash-matching against known illegal content databases (limited by encryption), and sanctions screening to enforce the AUP.

The encryption architecture means that the AUP cannot be comprehensively enforced for content restrictions — the platform's primary enforcement lever for content violations is account-level action based on credible reports, not content-level inspection.

### Enforcement

| Violation | Action |
|---|---|
| First credible report of AUP violation | Account temporarily restricted; tenant notified and given opportunity to respond within 5 business days |
| Second confirmed AUP violation | Immediate suspension; formal dispute process available |
| CSAM or terrorism-related material | Immediate termination without notice; law enforcement reporting as required |
| Sanctions-related violation | Immediate account freeze; data access suspended pending legal review |

---

## Legal Review Requirements

Before the ToS and AUP are published and tenants are accepted:

- Both documents must be reviewed by qualified legal counsel in the platform's operating jurisdiction.
- The arbitration clause and class action waiver must be reviewed for enforceability in the US, EU, and UK at minimum.
- The DMCA agent must be formally registered.
- A separate Privacy Policy must be drafted and published.
- If the platform intends to accept tenants in the EU before DPA templates are finalized, a legal hold must be placed on EU tenant acceptance until the DPA is ready (see [GDPR and Data Privacy Regulations](./gdpr-and-data-privacy-regulations.md)).
