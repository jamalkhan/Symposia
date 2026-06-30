# User Profile Visibility

## Overview

Individuals must be able to see what data is held about them, which marketers have a record linked to their identity, and what those records contain. This is both a legal requirement (GDPR Article 15 right of access, CCPA right to know) and a core platform value: transparency is how Symposia earns individual trust.

The vehicle for this is the **Symposia Profile Portal** — a web interface where individuals with a Symposia identity can view and manage their presence on the platform.

---

## Profile Portal

The profile portal is accessible at `https://profile.symposia.network`. It requires login with the individual's Symposia account.

### What the Portal Shows

**My Data at Marketers**

A list of all marketer tenants who hold a contact record linked to this individual's Symposia identity, with for each marketer:
- Marketer name and logo
- Date the connection was established
- Data categories held (name, email, purchase history, behavioral data, custom properties)
- Active permissions granted to this marketer
- Last interaction date
- Options: manage preferences, view data, revoke permissions, request deletion

**My Behavioral Data**

A timeline of tracked events attributed to this Symposia identity across all marketers:
- Page views (with domain, date, time)
- Email opens and clicks
- Purchases (if the tracking pixel captured purchase events)
- Custom events from marketer trackers

The individual can filter by marketer and date range, and can delete specific event records or request bulk deletion.

**My Permission Grants**

A list of all permission grants — which marketers have been granted which permissions, when, and the source of the grant (form name, email campaign, etc.). The individual can revoke individual permissions here.

**My Tracking Settings**

Controls for cross-brand tracking:
- Symposia network tracking preference (allowed / brand_only / blocked)
- Individual browser fingerprint IDs associated with this identity (can be disassociated)
- Cookie status across known browsers/devices

**Third-Party Integration Disclosures**

For each marketer connected to the individual's identity, the portal shows which third-party integrations the marketer has activated and what data has flowed through them. This is aggregate data — the platform knows what was sent and when, but does not maintain a per-contact sync record.

Example disclosure:

> **Marketer: Walmart**
>
> | Integration | Status | Activated | Contacts Synced | Deactivated |
> |---|---|---|---|---|
> | Facebook Custom Audiences | Active | July 1, 2026 | 7,543,123 (ongoing) | — |
> | Google Customer Match | Inactive | July 1, 2026 | 7,543,123 (Jul–Sep 2026) | September 1, 2026 |

The platform cannot show the individual whether they specifically were included in a given sync, or what the third-party platform holds about them. The portal surfaces links to each platform's own data-access tools (Meta's Off-Facebook Activity, Google's My Ad Center, etc.) alongside the disclosure.

See [Ad Platform Integrations](../Integrations/ad-platform-integrations.md) and [Integrations Overview](../Integrations/integrations-overview.md) for the full model.

---

## What the Portal Does NOT Show

The portal shows data linked to the individual's Symposia identity (`symposia_identity_id`). It can also surface unlinked marketer contact records the individual may want to claim — because the platform indexes all collected identifiers at the point of collection (see below), it does not need to scan all tenant databases at claim time.

For the portal to show a marketer's data, the link must have been established through:
- The tracking system (same email in both the Symposia account and the marketer's contact record, matched via the tracker).
- The individual claiming the record manually via the claim flow (see below).
- The marketer's integration writing the `symposia_identity_id` to their contact record (with the individual's consent).

**Identifier index**: when any marketer collects an identifier from an individual (email, phone, or other contact address), the platform writes a record to a platform-level identifier index: `{ identifier_type, identifier_value_hash, tenant_id, collected_at }`. This index is not marketer-readable — it exists solely for the purpose of enabling the individual to discover which marketers hold a given identifier without requiring a full cross-tenant database scan at lookup time. Marketers who collect identifiers without classifying them correctly (i.e., storing an email in a non-identifier field to avoid indexing) are in breach of the acceptable use policy — see [Integrations Overview — Platform Policy](../Integrations/integrations-overview.md#platform-policy-integration-transparency-as-a-requirement) for the enforcement model.

The "claim my records" flow:
1. Individual enters an email address (or other identifier) in the portal.
2. The platform looks up the identifier hash in the platform-level identifier index — O(1) lookup, no cross-tenant scan required.
3. The individual receives a list of marketers who have a contact with that identifier (first-party collected only — see [Identity Proof and Claim](./identity-proof-and-claim.md#resolved-auto-link-on-first-party-claim)).
4. The platform sends a verification message to the identifier to confirm the individual controls it.
5. On confirmation, the `symposia_identity_id` is written to each matched contact record and the identifier is added to the individual's claimed surface set.

---

## Data Access API (Individual-Facing)

These endpoints require authentication as the individual (Symposia account session):

```
GET  /identity/profile                            Full profile summary
GET  /identity/marketers                          List marketers with linked records
GET  /identity/marketers/{tenant-id}/data         Full data held by this marketer
GET  /identity/marketers/{tenant-id}/permissions  Permission grants to this marketer
GET  /identity/events                             Cross-marketer event timeline
GET  /identity/events?tenant_id={id}              Events filtered to one marketer
DELETE /identity/events/{event-id}                Delete a specific event record
POST /identity/export                             Export all data (GDPR portability)
POST /identity/claim-records                      Claim unlinked contact records
PATCH /identity/tracking-preferences              Update tracking settings
```

---

## GDPR Article 15 — Subject Access Request (SAR)

A formal Subject Access Request under GDPR Article 15 requires the marketer to provide a copy of all data held about the individual within 30 days. The platform provides tooling to support this:

```
POST /marketing/contacts/{id}/sar-export
```

This generates a structured export of everything held in the marketer's database for that contact:
- Contact record (all fields, all custom properties)
- Compliance records (consent history)
- List memberships
- Event history (email activity, web tracking events attributed to this contact)
- Campaign send history

The export is delivered as a JSON file (machine-readable) and a human-readable HTML summary. The marketer can use this to respond to a SAR. The export is also available to the individual via the portal's "view data" option.

---

## Answered Questions
- **Marketer discoverability**: Should the portal allow an individual to see ALL marketers on the platform who might have their email (even without a Symposia link), or only linked marketers? Full discoverability would require scanning all tenant databases — expensive and privacy-sensitive in itself (if the search reveals that a company has data on you that you didn't know about, that could be alarming). The "claim my records" flow is a middle ground — individuals can check, but the platform doesn't proactively tell them.
> The portal should identify all marketers on the platform who have collected a given verified identifier (e.g., email, phone, etc.). To reduce the expensive operation of scanning all tenant databases, all collected identifiers will be saved to the platform on collection of identifier with a reference to ensure that this is known.
> Marketers should collect identifiers ONLY as identifiers. Collecting such identifiers without the proper classification is considered a breach of the usage policy, and is subject to penality including but not limited to financial penalty, dejection from the platform, and referral to law enforcement for breach of compliance (e.g., GDPR, CCPA, etc.)

- **Third-party ad platform data**: If a marketer has synced a contact to Facebook or Google Ads, the platform can show the individual that this sync exists (via the marketer's integration list), but it cannot show them what Facebook or Google holds. Should the platform surface third-party sync destinations even if it can't show the data?
> When a marketer activates platform features or applications, such as integrations with Facebook or Google, the platform will log feature activation and usage data, and this activation will be presented to the user. For example, if Marketer A turned on Facebook integration on 7/1/2026 and synchronized 7,543,123 contacts between 7/1/2026 and today AND Marketer A turned on Google Ads integration on 7/1/2026, sychronized 7,543,123 contacts between 7/1/2026 and 9/1/2026, but then deactivated it on 9/1/2026, this should be visible to the individual. Aggregate data only.