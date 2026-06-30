# Identity Proof and Claim

## The Problem

The current [User Data Ownership](./user-data-ownership.md) document establishes the blockchain wallet keypair as the root of Symposia identity. This is correct for the long-term architecture, but it assumes an individual has already established their wallet identity. In practice, most individuals will accumulate data in the system long before that — and many may never establish a wallet at all.

Data accrues under multiple distinct identity surfaces:

| Identity Surface | Examples | Who Creates It | Proof Mechanism |
|---|---|---|---|
| **Contact addresses** | Email address, phone number | Marketer (via import or form submission) | Receiving a message at that address (OTP / magic link) |
| **Push tokens** | APNs device token, FCM registration token | App SDK on device | Receiving a push notification on that device |
| **Mobile device identifiers** | Apple IDFA, Google GAID | App SDK on device, with user ATT permission | In-app session context (implicit via SDK) |
| **Tracking cookies** | `_sym_net` (Symposia cookie), `_sym_brand` (marketer cookie) | Browser JS tracker | Possessing the cookie in browser context |
| **Symposia network identity** | `symposia_identity_id` (UUID) | Platform (assigned when first Symposia signal is collected) | Linking via one of the above surfaces |
| **Wallet identity** | Blockchain wallet address | Individual (self-generated or custodial) | Cryptographic signature (wallet private key) |

The wallet is the only identity surface the individual *creates* and *controls* from the start. Every other surface is created by a third party (the marketer, the platform, the operating system) and *associated* with a person — not owned by them in a cryptographic sense until they claim it.

This creates an **identity lifecycle problem**:

```
Phase 1: Anonymous
  Browser visits marketer's site
  → _sym_net cookie assigned (no wallet, no email, no name)
  → page view events recorded against cookie ID

Phase 2: Contact Acquisition
  Visitor submits email at checkout
  → Marketer contact record created (email, name, maybe phone)
  → Contact linked to cookie ID (session continuity)
  → But still no wallet — no cryptographic ownership established

Phase 3: Claimed Identity (may never happen)
  Individual creates Symposia account (wallet established)
  → Wants to claim historical cookie data and marketer contacts
  → Must PROVE they are the same person who owned each surface
```

The question this document addresses: **how does an individual prove they own an identity surface, and what do we do with surfaces they never claim?**

---

## Why This Matters

Without a clear answer, several promises in the data ownership model break:

- **Right to deletion** — an individual emails support@symposia asking to delete their data. We need to know which marketer contact records, which tracking cookie histories, which push token records are actually theirs. Without verified ownership of each surface, we cannot safely delete (we might delete the wrong person's data) or verify completeness (we might miss records they actually own).
- **Right to access / "who has data on me"** — the [profile visibility](./user-profile-visibility.md) portal shows which marketers hold data linked to the individual. For this to be meaningful, the individual must have verified which surfaces belong to them.
- **Cross-brand data propagation** — deletion requests and permission revocations are supposed to propagate across all marketer contact records linked to the same `symposia_identity_id`. That propagation is only correct if the identity links are verified.
- **Consent meaningful-ness** — a permission grant signed by a wallet keypair is cryptographically strong. A permission grant stored against an email address that was never verified is not — anyone could claim that email, or the email could have changed hands.

---

## Identity Surfaces in Detail

### Contact Addresses (Email, Phone)

Created when a marketer imports or collects an individual's contact information. The most common entry point.

**Ownership proof options:**
- **OTP via the channel**: send a one-time code to the email or phone; entering it proves inbox/SIM access. This is the industry standard and should be the default.
- **Magic link**: a signed URL sent to the address; clicking proves the recipient opened it in a live browser session. Simpler UX than entering a code, but does not prove the person is the same one who later claims ownership — just that someone with inbox access clicked the link.

**Complications:**
- Corporate/shared email addresses (info@, billing@) may be accessed by multiple people.
- Phone numbers are recycled by carriers; a number may have changed hands.
- An individual may have given a marketer a different email than they use for their Symposia account — but they should still be able to claim both as theirs (e.g., Jamal has both jamal@gmail.com and jamal@hotmail.com, both are his).

### Push Notification Tokens

Created by the OS when the individual installs an app and grants notification permission.

**Ownership proof:** possession of the token in an active app session is implicit proof of ownership. If the app SDK has authenticated the user's wallet or email, the push token is automatically linked at session time. For unauthenticated users, the push token is essentially a device cookie — ownership proof requires either a future authenticated session or a challenge-response (send a push with a code; user enters code in a browser to link the token to their wallet).

**Complications:** push tokens rotate (especially after app reinstall or OS update). A token-to-identity link is inherently time-bounded and must be revalidated periodically.

### Mobile Device Identifiers (IDFA / GAID)

The Apple IDFA and Google GAID are advertiser-scoped device identifiers. Post-ATT (Apple App Tracking Transparency), IDFA is opt-in and resets on user request. GAID is similar.

**Ownership proof:** in-app session context. The app SDK reads the IDFA/GAID and can link it to an authenticated session within the same app. Outside the app context, device IDs are not claimable — there is no verification flow that can prove "I own device ID X" from a browser session.

**Complications:**
- IDFA is increasingly restricted on iOS; it will frequently be unavailable.
- Device IDs can be reset by the user deliberately — this severs existing links and should be treated like a cookie deletion.
- Symposia should treat device IDs as **medium-strength, device-scoped identifiers** rather than durable person identifiers. They are useful for in-app attribution but not reliable as long-term identity anchors.

### Tracking Cookies (`_sym_net`, `_sym_brand`)

Set in the browser by the JS tracker when the individual visits a marketer's site. The Symposia cookie (`_sym_net`) is the network-level identifier; the marketer cookie (`_sym_brand`) is the brand-level identifier.

**Ownership proof:** possession of the cookie in the current browser session. This is the weakest proof surface — it proves "this browser session has access to this cookie" but not "I, a specific person, own this cookie's history." Cookies can be:
- Cleared by the user (severs the link permanently)
- Blocked by the browser (Safari ITP, Firefox ETP)
- Present on a shared device (family computer, work machine)
- Theoretically exfiltrated via XSS (though `httpOnly` mitigates this)

Cookies are best treated as **session continuity identifiers**, not durable identity claims. Their value is tracking an individual's behavior within and across sessions *before* a stronger identity is established. Once a stronger identity (email OTP, wallet) is linked to a cookie, the cookie becomes an alias of that stronger identity, not a primary identity in its own right.

---

## The Unclaimed Identity Problem

The most architecturally important question: what do we do with identity surfaces that are never claimed?

A significant fraction of contacts in any marketer's database will never establish a Symposia wallet. Their contact record may have an email and a cookie history, but nothing is cryptographically verified. These are "unclaimed" identities.

Options for handling unclaimed identities:

**A. Treat email OTP as sufficient for marketer-scoped rights**
An individual can exercise deletion and access rights against a single marketer's contact database by verifying their email address via OTP — no wallet required. This is a weaker guarantee (email access ≠ wallet ownership) but it is widely legally sufficient (GDPR deletion rights are exercised this way today across the industry) and much lower friction for individuals who will never use a blockchain wallet.

**B. Auto-expiry of unclaimed tracking data**
Cookie-based tracking data that has never been linked to a contact address (i.e., purely anonymous) auto-expires after N days (e.g., 13 months, matching GA4's default). The individual had no meaningful ownership claim over this data anyway, and retention beyond what's operationally useful creates liability without value.

**C. Tag all unclaimed data with a "claim pending" status**
Every piece of data collected against an unclaimed surface gets a `claim_status` flag. Unclaimed data cannot be used for cross-brand enrichment, cannot appear in profile visibility portals, and cannot be subject to wallet-gated rights flows. It *can* be used for the marketer's own internal analytics and segmentation (the marketer's first-party use case doesn't require verified individual ownership).

**D. Probabilistic pre-linking with explicit confirmation required**
The platform may detect with high probability that two surfaces belong to the same person (e.g., a cookie and an email address submitted in the same session). It pre-links them in a "candidate link" state. The individual must explicitly confirm the link (via OTP or wallet signature) before the link becomes authoritative for rights-exercise purposes. Until confirmed, the platform can use the probabilistic link for marketer personalization (lower stakes) but not for data deletion propagation (higher stakes).

---

## Options / Approaches for the Full Design

These are not mutually exclusive — the likely final design combines elements of several.

---

### Option 1: Tiered Identity Strength `[Priority: Now]`

Define a formal hierarchy of identity assurance levels, and tie platform capabilities to the required level:

| Tier | Identity Basis | How Established | Capabilities Enabled |
|---|---|---|---|
| **T0 — Anonymous** | Tracking cookie / device ID only | Browser visit / app install | Per-marketer analytics, session continuity only |
| **T1 — Verified Contact** | Email or phone, OTP-verified | OTP flow from marketer's form or platform | Marketer-scoped deletion, marketer-scoped access, unsubscribe, subscription management |
| **T2 — Symposia Account** | Symposia wallet (custodial or self-custody) | Wallet creation + at least one verified contact | Cross-brand profile visibility, cross-brand deletion propagation, consent grant management, data portability |
| **T3 — Cryptographic Ownership** | Wallet signature over all claimed surfaces | Explicit wallet-signed claim for each surface | Maximum: all T2 rights + on-chain consent tokens + capability token issuance |

Capabilities map to tiers: deletion from a single marketer requires T1. Cross-brand deletion requires T2. Issuing or revoking on-chain capability tokens requires T3.

This is pragmatic — most individuals will live at T1 (email-verified) and that covers the majority of their practical rights needs. T3 is the full architecture for individuals who want cryptographic sovereignty.

**Trade-off**: tiering adds design surface area and means the "wallet is the root of identity" principle is softened in practice. But it matches how the real world works today and gives the platform a migration path toward fuller cryptographic guarantees over time.

---

### Option 2: Identity Claim Flow `[Priority: Now]`

A dedicated flow allowing individuals to claim identity surfaces into their wallet after the fact. This is a post-acquisition verification process:

```
1. Individual creates Symposia wallet (T2/T3 established)
2. In their profile portal, they see a "Claim your identities" section
3. They add an email address → platform sends OTP → they enter it → email linked to wallet
4. They add a phone number → SMS OTP → linked
5. Historical tracking cookies are claimed by: visiting a "link this browser" URL while logged into their wallet in the same browser session
6. Past marketer contact records linked to the email are now also linked (via existing email→contact_id mappings)
```

This approach is compatible with Option 1 (tiered identity strength) — it's the mechanism by which someone moves from T1 to T2/T3.

**Resolved: auto-link on first-party claim.** When an email is verified into a wallet (via explicit claim flow or federated verification), all marketer contact records where the marketer has a **first-party claim** on that email are automatically linked and surfaced to the individual. A first-party claim means the marketer collected the email directly from the individual through a direct interaction — a purchase, account signup, form submission, etc. Contact records that arrived via purchased lists, third-party data brokers, or data appends are **not** considered first-party claims and are not auto-linked. This distinction does two things: it reinforces the platform's trust posture (only legitimate relationships are surfaced), and it creates a compliance incentive for marketers to accurately track their acquisition source.

The individual sees a summary view upon claiming: "This email is used in 4 brands where you have a direct relationship — Walmart, Hyatt, Malamute Adventures, and 1 other." No additional per-marketer confirmation step is required. The link is made authoritative from that point. If the individual does not recognize a brand listed, they have the option to dispute the link (see Adversarial Scenarios — this is the "marketer incorrectly classified a list import as first-party" case).

---

### Option 3: The "Data Escrow" Model for Pre-Wallet Data `[Priority: Now]`

Rather than tying pre-wallet data to the eventual wallet, treat all pre-wallet data as held in escrow:

- Data is collected normally against cookie/email surfaces.
- It is flagged as `escrow: true` — held for the individual's eventual claim, but not yet authorized by a wallet.
- When a wallet is established and surfaces are claimed, the escrow data is transferred into the wallet-owned identity.
- Escrowed data that is never claimed is auto-purged after a retention window (e.g., 24 months).
- Escrowed data **can** be used by the marketer for their own first-party purposes (it is their created/derived data) but cannot be used for cross-brand enrichment or Symposia network-level features until claimed.

**Advantage**: preserves the wallet-as-root-of-identity principle rigorously — the wallet is always the authoritative owner. No data is ever treated as "claimed" without explicit individual action.

**Disadvantage**: most data will never be claimed (most individuals will not establish wallets in the near term). Escrowed data sitting unclaimed for 24 months that then auto-purges is operationally simple but may frustrate marketers who have invested in building a contact record that disappears when the individual doesn't act. Need to distinguish between escrow rules for tracking data (cookie history, page views) vs. marketer-created data (orders, purchase history) — the latter is the marketer's data to keep as long as business needs require, subject to deletion requests; the former is more clearly "individual's data held by the platform."

---

### Option 4: Federated Verification (Leverage Existing Verifiers) `[Priority: Fast-follow]`

Rather than building all verification infrastructure from scratch, leverage existing identity verifiers:

- **Email**: via SMTP OTP (our own flow) or via OAuth sign-in with Gmail/Outlook/ProtonMail (the individual proves ownership by signing in with that provider).
- **Phone**: via SMS OTP (standard) or via carrier-level verification SDKs (e.g., Twilio Verify with SIM binding, where the carrier itself confirms the phone matches the SIM card in the requesting device).
- **Apple IDFA / device**: via Sign in with Apple — Apple has already verified the individual controls this Apple ID and its associated device.
- **Google GAID / device**: via Sign in with Google.
- **Social**: via OAuth (Facebook Login, etc.) — proves ownership of a social account, which may be a meaningful identity signal.

**Advantage**: each of these providers has already done the identity verification work and carries significant accountability. An "Email: verified by Google OAuth" claim is a stronger signal than "email: we sent an OTP and someone clicked it."

**Disadvantage**: this creates dependencies on third-party providers, which conflicts somewhat with the decentralized ethos of the platform. A Google account verification is only as durable as the individual's Google account — if they lose access, so does their claim. It also means Google knows when someone is claiming an identity on Symposia.

---

### Option 5: Progressive Trust — No Explicit Claim Required `[Priority: Fast-follow]`

Instead of a formal claim flow, design the system so that trust is accumulated progressively through consistent behavior:

- A Symposia cookie + email contact in the same session → automatic candidate link (T0.5).
- Email OTP completed (from any context — marketer's welcome email, preference center, etc.) → promoted to T1.
- Wallet creation + signing a claim with the wallet → T3.
- Consistent signals over time (same device, same browser, same email, no conflicts) increase confidence in candidate links without explicit confirmation steps.

The individual is only prompted to confirm a link when they attempt an action that requires a higher assurance level — e.g., when they try to exercise cross-brand deletion (requires T2), the platform explains "to delete across all brands, connect your email to a Symposia account" and walks them through it.

**Advantage**: minimal friction for the common case; the individual doesn't need to understand identity tiers or do explicit linking until they need to.
**Disadvantage**: probabilistic linking can make mistakes, and mistaken links have real consequences (deleting the wrong person's data). Requires a robust conflict-detection mechanism and a process for adjudicating disputed links.

---

## Adversarial Scenarios to Design Against

Any design must handle these:

| Scenario | Risk | Mitigation |
|---|---|---|
| **Someone claims an email they don't own** | Exercise deletion on someone else's account; access someone else's data | OTP verification ensures inbox access; only the current inbox holder can claim |
| **A marketer's staff member claims a customer's email** | Staff member links a customer contact to their own wallet, gaining rights over the customer's data | OTP must go to the claimed address, not to any internal email; rate limiting; audit log |
| **Shared device / family computer** | One family member claims the other's cookies | Cookies are device/browser scoped; no cookie can grant rights over data held by another surface that wasn't in the same session. Cookie-only (T0) is explicitly insufficient for rights exercise |
| **Shared corporate email** | `billing@company.com` claimed by one employee; company disputes it | Corporate email addresses are a hard case. Policy option: addresses matching a domain pattern (e.g., `.com` business domains) may be limited to T1 (marketer-scoped) rights only, not T2/T3 cross-brand claims |
| **Email address recycled by provider** | Old email owner accumulated data; new holder claims it via OTP | The new holder passes OTP verification and gets access. The previous holder's data is now in limbo. Mitigation: at the point a new holder claims an address that has existing data under it, surface a notice ("this address has existing data — do you want to import it or treat this as a new identity?") and require explicit acknowledgment |
| **Cookie exfiltration via XSS** | Attacker gets the cookie value and attempts to claim the identity using it | Cookies are `httpOnly` and `SameSite=Strict`; cookie possession alone (T0) is not sufficient for rights-exercise actions; those require T1+ with OTP. The primary damage from cookie exfiltration is tracking visibility, not identity takeover |

---

## Open Questions

1. **Minimum identity for rights exercise**: What is the lowest-friction path to exercising a deletion right? Is email OTP sufficient for a deletion request against a single marketer, or does Symposia require a full account (wallet)?

2. **Auto-claiming at contact acquisition**: When a marketer collects an email via a verified OTP flow (e.g., a double opt-in welcome series), should that OTP verification count as a T1 identity claim, or does the individual need to re-verify separately through Symposia's own flow?

3. **Unclaimed data retention policy**: How long do we hold anonymous tracking data (cookie-only, no contact address) before auto-purging? 13 months (GA4 standard)? 24 months? This has both privacy and marketer analytics implications.

4. ~~**Data-to-identity link on claim**~~ **Resolved**: First-party marketer contact records (those where the marketer collected the email directly from the individual) auto-link and are surfaced to the individual upon email claim, with a summary view and the ability to dispute any unrecognized brand. Non-first-party records (list purchases, broker data) do not auto-link. See Option 2.

5. **Conflict resolution**: Two people claim the same email (e.g., after a provider recycles an address). What does the platform do? First-claimant wins? Require re-verification after N months?

6. **Corporate vs. personal address handling**: Is there a distinction between personal email addresses and corporate/alias addresses in terms of what identity tier they can support?

7. **Platform liability for incorrect links**: If the platform incorrectly links a surface to the wrong wallet (probabilistic linking gone wrong), and data is deleted or accessed as a result, what is the remediation process?

---

## Direction

All five options will be implemented. Options 1, 2, and 3 are the foundation and will be built first. Options 4 and 5 are fast-follow enhancements layered on top.

**Now (Options 1 + 2 + 3):**

- **Tiered identity strength (Option 1)** is the structural backbone. Every platform capability that touches rights exercise or cross-brand data is gated to an identity tier. T1 (email OTP) unlocks marketer-scoped rights. T2/T3 (wallet) unlocks cross-brand features. No individual is required to have a wallet to unsubscribe, delete from a single marketer, or access their data at one brand.
- **Explicit claim flow (Option 2)** is how an individual assembles their full identity across surfaces. Email → OTP → linked. Phone → SMS OTP → linked. Browser cookies → "link this browser" URL while logged in → linked. Upon claiming an email, all marketer contact records where the marketer has a **first-party claim** (direct collection: purchase, signup, form submit) are auto-linked and shown to the individual in a summary view. Non-first-party records (purchased lists, broker data) are not surfaced. This reinforces the platform's trust posture and creates a compliance incentive for marketers to track acquisition source accurately.
- **Data escrow (Option 3)** handles the pre-wallet period. All tracking data (cookie-based, anonymous) is held in escrow — usable by the marketer for their own first-party analytics, not eligible for cross-brand enrichment or Symposia network features. Unclaimed tracking escrow auto-purges after a defined retention window (exact window is [open question #3](#open-questions)). Marketer-created data (orders, purchase history) is not subject to escrow purge — it is the marketer's own business record and follows the [created-data ownership rules](../MarketingData/contact-database.md#data-ownership-model).

**Fast-follow (Options 4 + 5):**

- **Federated verification (Option 4)** elevates proof strength for email and phone. Rather than relying solely on OTP delivery (which proves inbox access, not ongoing account control), OAuth with Gmail/Outlook/ProtonMail and carrier-level SIM binding provide stronger, provider-attested verification. Sign in with Apple / Sign in with Google also cover device-level claims. Priority: implement after the core OTP claim flow is stable, as it adds provider dependencies and UX complexity that should not block the foundation.
- **Progressive trust accumulation (Option 5)** reduces friction by surfacing claim prompts contextually rather than requiring a dedicated identity-management session. When an individual clicks an unsubscribe link, the platform notices they are at T0/T1 and prompts: "want to manage preferences across all your brands?" When an individual completes a purchase at a marketer, the platform can prompt wallet creation in the post-purchase confirmation flow. This option is a UX and conversion-rate improvement on top of Options 1–3, not a distinct verification mechanism. Priority: design and implement once the core claim flow (Option 2) is instrumented well enough to identify where individuals drop off.

**Implication for downstream specs**: [Right to Delete](./right-to-delete.md), [Subscription Management](./subscription-management.md), and [User Profile Visibility](./user-profile-visibility.md) must all be designed with the T0/T1/T2/T3 tier model in mind — specifically, each flow must define what identity tier it requires and what it degrades to when the individual is below that tier.
