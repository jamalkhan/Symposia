# Content Moderation and Legal Policy

## Overview

This is the hardest policy problem the platform faces. The encryption architecture — which provides the privacy guarantee tenants need — also means the platform cannot inspect the content stored on its network. A node operator cannot read what they store. The platform cannot scan for illegal content. This is a feature, not a bug, for legitimate users. But it creates a real legal and ethical challenge.

This document defines the platform's position, the limits of what it can do, and the mechanisms available to act when legally required.

**This document requires review and sign-off from qualified legal counsel before the platform accepts public users.**

---

## The Fundamental Tension

The platform's architecture creates a genuine conflict between two legitimate goals:

1. **Privacy**: Data is encrypted with tenant-managed keys. The platform cannot read content. This is the core value proposition and cannot be compromised without destroying trust.
2. **Legal compliance**: Some content is illegal in virtually all jurisdictions (CSAM). Some users may be sanctioned individuals or entities. Some content may violate court orders. Platforms are generally expected to have mechanisms to address these.

There is no technically honest way to fully satisfy both simultaneously. This document describes the position the platform takes and the limited tools available.

---

## What the Platform Can and Cannot Do

### Cannot Do

- Inspect, scan, or filter encrypted content stored on storage nodes.
- Identify whether a specific blob contains illegal material by reading it.
- Automatically detect and remove illegal content the way a traditional cloud provider might.

### Can Do

- Terminate tenant accounts that are reported to store illegal content via a legal process or credible notice.
- Deny service to individuals and entities on applicable sanctions lists (OFAC, EU, UN).
- Enforce Terms of Service and close accounts where there is documented evidence of abuse.
- Cooperate with lawful legal processes directed at metadata (blob hashes, sizes, access timestamps, tenant identity) — noting that metadata reveals nothing about content.
- Remove access to specific blobs by hash if a court order provides the hash of a specific piece of illegal content. This prevents reupload of known-hash illegal content without reading new content.

---

## Terms of Service

The Terms of Service must clearly and unambiguously prohibit:

- Storage of child sexual abuse material (CSAM) or any content involving the sexual exploitation of minors.
- Storage of content that facilitates terrorism, mass violence, or genocide.
- Use of the platform by individuals or entities subject to applicable sanctions.
- Storage of content that violates a court order in a jurisdiction where the platform operates.
- Use of the platform to facilitate fraud, ransomware, or other criminal activity.

Violation of any of the above is grounds for immediate account termination without refund. Remaining credits are forfeited.

The Terms of Service are subject to legal review and must be clear about the jurisdictions in which they are enforceable.

---

## Sanctions Compliance

- Before activating any tenant account, the platform screens the registrant's identity against applicable sanctions lists (OFAC SDN list, EU Consolidated List, UN Security Council list).
- Sanctions screening is repeated periodically for existing accounts and triggered by account changes (name, address, payment method).
- An account that matches a sanctions list entry is immediately suspended pending review. The tenant is notified unless notification itself is prohibited by the applicable sanctions regime.
- Sanctions compliance is a legal requirement in any jurisdiction where the platform operates and has no opt-out.

---

## CSAM — The Hard Case

CSAM is the most serious category and the one most likely to create legal obligations even for platforms that cannot read content.

### Position

The platform cannot proactively scan for CSAM because data is encrypted. This is a known and stated limitation. However:

- The platform cooperates fully with law enforcement agencies presenting valid legal process (court orders, search warrants from applicable jurisdictions) directed at account metadata.
- If law enforcement provides a hash of known CSAM (e.g., from the NCMEC hash database), the platform will reject any upload whose declared content hash matches a known CSAM hash. This is hash-matching, not content scanning — it catches reuploads of known material without ever reading new content.
  - **Limitation**: A tenant encrypting content client-side before upload means the platform receives encrypted ciphertext. The hash of the ciphertext will not match the hash of the plaintext CSAM. This technique only works for unencrypted uploads or for detecting known ciphertext hashes.
- The platform reports CSAM to NCMEC (National Center for Missing and Exploited Children) when it becomes aware of it through legal process, as required by 18 U.S.C. § 2258A.

### Legal Counsel Requirement

The exact obligations vary by jurisdiction. In the US, 18 U.S.C. § 2258A creates specific reporting requirements. In the EU, the proposed CSAM regulation (at time of writing, still in development) may create additional obligations. **Legal counsel must advise on the specific obligations before launch.**

---

## Responding to Legal Demands

### What Can Be Produced

When the platform receives a valid legal demand (subpoena, court order, search warrant from a competent jurisdiction):

The platform **can** produce:
- Tenant identity information (name, email, billing address, payment records).
- Account creation date and access timestamps.
- Blob metadata: key names, sizes, upload timestamps, region assignments, access logs.
- IP addresses used to access the account.

The platform **cannot** produce:
- Decrypted blob content (it does not possess decryption keys).
- Plaintext of any user data.

This is disclosed publicly and in the Terms of Service. The platform will not make representations to law enforcement that it can produce content it does not possess.

### Transparency Reporting

The platform publishes an annual transparency report including:
- Number of legal demands received by jurisdiction.
- Number complied with, challenged, or rejected.
- Number of account terminations due to legal process.
- Number of accounts suspended due to sanctions screening.

This is consistent with the no-backdoor principle: transparency reporting makes government overreach visible to the public.

### Jurisdiction and Choice of Law

The platform must be incorporated in and operate under the laws of a jurisdiction that:
- Provides meaningful legal protections for privacy and allows challenge of overbroad legal demands.
- Does not require the platform to install backdoors or undermine encryption (e.g., not subject to a Five Eyes mandatory access law).

**This is a founding decision that requires legal counsel and directly affects the platform's ability to uphold its no-backdoor guarantee.**

---

## Abuse Reporting

- A publicly accessible abuse reporting mechanism allows third parties to report suspected Terms of Service violations.
- Reports are reviewed by a human. The platform does not act on unverified reports automatically.
- Reports of CSAM are escalated immediately and treated as the highest priority.
- Frivolous or bad-faith abuse reports (e.g., competitors trying to disrupt a legitimate account) are logged and may result in the reporter being banned from using the reporting mechanism.
- The platform does not disclose to the reported tenant that an abuse report was made unless and until account action is taken.
