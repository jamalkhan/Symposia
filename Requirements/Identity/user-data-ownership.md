# User Data Ownership

## The Core Principle

**Users own their data. Marketers are granted permission to use it.**

This is what differentiates Symposia from every incumbent martech platform. In the current internet, companies collect behavioral data, build profiles, and trade them — the individual has no visibility, no control, and no recourse except opaque legal mechanisms that rarely work. Symposia inverts this.

Every piece of data collected about an individual through the Symposia network belongs to one of two layers, defined precisely in [Stakeholders and Personas — General Ownership Rule](../Platform/stakeholders-and-personas.md#general-ownership-rule): the **identity layer** (name, email, phone, address — directly identifying attributes the individual owns outright) and the **created/derived layer** (order history, purchase behavior, engagement scores, ML-derived attributes — owned by whichever marketer or appbuilder created them, per [Contact Database — Data Ownership Model](../MarketingData/contact-database.md#data-ownership-model)). Marketers have access to identity-layer data because the individual has granted permission, explicitly or through recognized implied consent mechanisms. When permission is withdrawn, access ends. When the individual requests deletion: identity-layer data is deleted outright; created/derived-layer data is anonymized or pseudonymized so it can no longer be tied back to the individual (see [Erasure and the Created-Data Layer](../MarketingData/contact-database.md#erasure-and-the-created-data-layer)).

This is not just a legal compliance mechanism. It is the product's fundamental value proposition: marketers who use Symposia have access to higher-quality, higher-trust data because consumers will actually engage with platforms that respect their ownership. Consent-based marketing consistently outperforms surveillance-based marketing on engagement metrics.

The architecture must enforce this at the data layer — not as a policy that could be overridden by a business decision, but as a structural constraint.

---

## Identity Layers

There are two distinct identity scopes in the system:

### 1. Marketer-Level Identity (Contact Record)

A marketer creates a contact record for each person they interact with. This record lives in the marketer's [Contact Database](../MarketingData/contact-database.md) and is under the marketer's operational control.

- The marketer can create, update, and delete contacts.
- The marketer sets custom properties relevant to their business.
- The marketer tracks engagement with their own content (emails, pages, purchases).
- This data is scoped to the marketer. Other marketers cannot see it.
- The individual has rights over this data (see [Right to Delete](./right-to-delete.md)).

### 2. Symposia Network Identity (Cross-Brand Profile)

The Symposia network identity is the individual's identity across all marketers on the platform. It is created by the individual — not by any marketer — when they register for a Symposia account or when the tracking system collects enough signals to establish a persistent identity.

- The Symposia identity is controlled by the individual.
- Marketers see only the data the individual has permitted them to see.
- The individual can revoke a marketer's access to their Symposia identity at any time.
- The Symposia identity is the vehicle for cross-brand data rights (deletion requests that propagate across all marketers who hold a record linked to this identity).

**Resolved: The Symposia identity is tied to a blockchain wallet keypair.** This is not a configuration option — it is the architecture.

The individual's wallet private key is the root of their identity. Their wallet public key is their unique identifier on the network. Consent grants, permission records, and data-sharing capability tokens are all recorded on-chain, linked to the individual's wallet address.

This design enforces data ownership at the cryptographic level, not the policy level. A marketer cannot access an individual's data without a valid capability token. A capability token cannot be issued without a consent grant. A consent grant cannot be recorded without the individual's wallet signature. The chain of authorization is cryptographically verifiable end-to-end.

**UX for non-crypto-native individuals**: The wallet is embedded in the Symposia client (web, mobile). Users do not interact with it as a "blockchain wallet" — they experience it as a secure account with a recovery phrase. Standard web2-style onboarding (email + password) is offered as a UX layer on top of the wallet, with the wallet generated and managed transparently. Users who want self-custody can export their keypair at any time. Custodial wallet mode (platform holds an encrypted backup of the private key, recoverable via email) is available for users who opt in, with a clear disclosure that this mode reduces their sovereignty guarantee.

See [Security](../Platform/security.md) for the cryptographic details of how individual data encryption and marketer key-sharing work.

---

## What Data is Collected

The platform collects data about individuals at multiple points:

| Data Source | Data Collected | Who Controls | Individual Can |
|---|---|---|---|
| Email engagement (open, click, bounce) | Event + timestamp per message | Marketer | Opt out of tracking; delete event history |
| Web tracking (page views, events) via JS tracker | Page URL, event type, timestamp, device/browser fingerprint | Marketer (brand-level) + Symposia (network-level) | Block Symposia cookie; delete tracking history |
| Contact form / sign-up | Whatever the marketer collects (name, email, etc.) | Marketer | Update or delete |
| Purchased / imported by marketer | Whatever was in the import | Marketer | See what was imported; delete |
| Symposia account profile | What the individual self-reports | Individual | Full control |

---

## Permission Model

### Marketer Permission Types

| Permission | What It Grants | How It's Established |
|---|---|---|
| `email_marketing` | Send marketing email | Express or implied consent (per [Email Compliance](../Messaging/email-compliance.md)) |
| `email_transactional` | Send transactional email (receipts, alerts) | Implied by business relationship |
| `sms_marketing` | Send marketing SMS | Explicit opt-in required everywhere |
| `web_tracking_brand` | Track individual's behavior on marketer's own domain via first-party cookie | Disclosed in cookie consent banner |
| `web_tracking_network` | Contribute data to Symposia's cross-brand identity graph | Requires explicit Symposia consent, separate from brand cookie consent |
| `data_read` | Read individual's Symposia-level profile attributes | Requires individual to grant this permission explicitly to this marketer |
| `data_enrichment` | Add data to individual's Symposia-level profile | Requires explicit grant; most restrictive |

### Permission Grants

An individual's permission to a marketer is stored in a permission grant record on the individual's Symposia identity:

```json
{
  "identity_id": "uuid",
  "marketer_tenant_id": "tenant_01abc",
  "marketer_name": "Malamute Adventures",
  "permissions": ["email_marketing", "web_tracking_brand"],
  "granted_at": "2026-03-15T10:23:00Z",
  "grant_source": "checkout_form",
  "grant_wording": "I agree to receive marketing emails.",
  "revoked_permissions": [],
  "revoked_at": null
}
```

Revoking a permission removes the marketer's ability to use that channel. Revocation does not automatically delete the contact record from the marketer's database — that requires a separate deletion request. However, sending to a revoked contact is a violation that the platform enforces technically.

---

## Cross-Tracking Controls

The individual controls whether the Symposia network cookie tracks them across brands.

**Setting: Symposia network tracking**
- `allowed`: The Symposia cookie is set and the individual's cross-brand behavioral data contributes to the network identity graph. The individual can see which brands have contributed data (via the profile portal).
- `brand_only`: First-party cookies are permitted (brand-by-brand), but the Symposia network cookie is not set. Each brand sees only their own interactions.
- `blocked`: No Symposia network tracking. First-party cookies are still the marketer's prerogative under their own cookie consent.

The `blocked` setting is enforced at the tracker level — the JavaScript snippet checks for this preference (via the Symposia identity API or a locally stored consent token) and does not write the Symposia network cookie if set.

The default for new visitors is `brand_only` — the Symposia network cookie requires opt-in, not opt-out. This is aligned with GDPR's consent requirement for third-party tracking.

---

## Individual Rights Summary

| Right | Source Law | Mechanism |
|---|---|---|
| Right to access | GDPR Art. 15, CCPA | View profile portal — shows all data held across linked marketers |
| Right to rectification | GDPR Art. 16 | Update profile portal — changes propagate to linked marketer records |
| Right to erasure | GDPR Art. 17, CCPA, others | Deletion request — see [Right to Delete](./right-to-delete.md) |
| Right to data portability | GDPR Art. 20 | Export all data from profile portal |
| Right to object to marketing | GDPR Art. 21 | Unsubscribe from any or all marketers — see [Subscription Management](./subscription-management.md) |
| Right to withdraw consent | GDPR Art. 7(3) | Revoke permission grant — same as opt-out |
| Right to know who has data | CCPA, various | Profile visibility — see [User Profile Visibility](./user-profile-visibility.md) |
| Right to restrict tracking | GDPR Art. 21(2), PECR | Cross-tracking controls (above) |

---

## What the Platform Does NOT Do

- **The platform does not sell contact data between marketers.** A contact record created by Marketer A is never shared with Marketer B, even if both have a contact record for the same individual.
- **The platform does not build a shadow profile without consent.** The Symposia network identity is only populated when the individual grants `web_tracking_network` permission.
- **The platform does not use individual data to train models sold to third parties.** Aggregated, anonymized data may be used for platform health metrics; individual-level data is not used beyond the individual's granted permissions.
- **The platform cannot be compelled to hand over decrypted individual data.** The no-backdoors guarantee (see [Security](../Platform/security.md)) extends to individual profile data. Encrypted at rest with user-controlled keys, the platform has nothing to hand over even under a lawful order.

This last point is structurally significant: in Symposia, law enforcement cannot compel the platform to produce an individual's behavioral profile because the platform does not have unencrypted access to it.
