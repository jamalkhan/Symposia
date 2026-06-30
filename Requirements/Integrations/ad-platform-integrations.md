# Ad Platform Integrations

## Overview

Ad platform integrations allow marketers to sync contact lists and audiences from Symposia to external advertising platforms. The primary use cases are:

- **Custom audiences**: uploading a list of existing customers to an ad platform so they can be targeted with ads on that platform
- **Lookalike audiences**: using a custom audience as a seed for the ad platform to find similar users
- **Exclusion lists**: syncing suppressed or unsubscribed contacts to exclude them from ad targeting (honoring opt-outs across channels)
- **Conversion signals**: sending purchase or event data back to the ad platform for attribution and campaign optimization

All of these involve **data egress** — contact data leaving Symposia and entering a platform Symposia does not control. For the platform's general policy on integration logging, individual visibility, and consent requirements, see [Integrations Overview](./integrations-overview.md).

---

## How Ad Platform Matching Works

Ad platforms do not receive raw PII directly. The standard mechanism is **hashed matching**:

1. The platform takes the contact's email address (and/or phone number)
2. It normalizes it (lowercase, strip whitespace)
3. It computes a SHA-256 hash of the normalized value
4. The hash list is uploaded to the ad platform
5. The ad platform matches the hashes against its own user database (also hashed)

The ad platform never receives the raw email address. Symposia never receives the ad platform's internal user IDs. The match happens inside the ad platform's systems.

**Privacy note**: hashed email addresses are still considered personal data under GDPR and most equivalent laws. The hash is a pseudonym — it cannot be reversed without a dictionary attack, but it is still uniquely identifying in practice. The consent requirements in [Integrations Overview — Consent and Legal Basis](./integrations-overview.md#consent-and-legal-basis-for-third-party-syncs) apply regardless of whether the data is hashed before transmission.

---

## Supported Platforms (Planned)

### Facebook / Meta Custom Audiences

**Integration type**: `facebook_custom_audiences`

Facebook Custom Audiences allows marketers to upload a hashed contact list to Meta's Ads Manager. Meta matches the list against Facebook and Instagram users and makes the matched audience available for ad targeting.

**Sync mechanism**: Meta Marketing API — Customer List Custom Audiences endpoint. Symposia handles the hash normalization and API authentication.

**Data fields sent**: hashed email, hashed phone (optional), hashed first name + last name + zip code (optional — improves match rate).

**Match rate expectations**: typically 40–70% of emails match a Facebook account, depending on the audience demographic.

**Individual disclosure in portal**:
- Integration name: "Facebook / Meta Custom Audiences"
- Activation date
- Aggregate contacts synced (total, and by sync date range)
- Deactivation date (if applicable)
- Note: "Symposia cannot show you whether you specifically were included in a sync or what Meta holds about you. Use [Meta's Off-Facebook Activity tool](https://www.facebook.com/off_facebook_activity) to see your activity there."

**Consent requirement**: `web_tracking_brand` minimum; for behavioral retargeting (e.g., "users who visited my site"), `web_tracking_network` or explicit retargeting consent required.

---

### Google Customer Match

**Integration type**: `google_customer_match`

Google Customer Match allows marketers to upload a hashed contact list to Google Ads. Google matches against signed-in Google users and makes the audience available for targeting across Search, YouTube, Gmail, and Display.

**Sync mechanism**: Google Ads API — Customer Match user list. Symposia handles normalization, hashing (SHA-256), and API authentication.

**Data fields sent**: hashed email, hashed phone (optional), hashed first name + last name + zip + country (optional).

**Eligibility requirements**: Google Customer Match has marketer-level eligibility requirements (account spend history, policy compliance). The Symposia integration surfaces eligibility status and surfaces the reason if a marketer's account is not eligible.

**Individual disclosure in portal**: same pattern as Facebook — activation date, aggregate sync counts, deactivation date if applicable, link to Google's "My Ad Center" for the individual to see their own Google ad profile.

**Consent requirement**: same as Facebook.

---

### TikTok Custom Audiences

**Integration type**: `tiktok_custom_audiences`

Similar to Facebook Custom Audiences — hashed contact list uploaded to TikTok Ads Manager for targeting on TikTok.

**Sync mechanism**: TikTok Marketing API.

**Status**: planned; not yet specced in detail.

---

### LinkedIn Matched Audiences

**Integration type**: `linkedin_matched_audiences`

LinkedIn Matched Audiences allows contact list upload for targeting LinkedIn members. Higher relevance for B2B marketers.

**Sync mechanism**: LinkedIn Marketing API.

**Status**: planned; not yet specced in detail.

---

### Pinterest Audiences

**Integration type**: `pinterest_audiences`

**Status**: planned; not yet specced in detail.

---

## Exclusion List Syncing

A particularly important use case for ad platform integrations is syncing **suppression and opt-out lists** back to ad platforms. This ensures that contacts who have opted out of email marketing are also excluded from ad targeting — honoring opt-out intent across channels, not just the specific channel where the opt-out was expressed.

The platform should support automatic exclusion list sync: when a contact is added to the suppression list (unsubscribed, hard bounced, complained), their hashed identifier is automatically sent to connected ad platform integrations as an exclusion.

This is a marketer-configurable option per integration, not enforced by the platform (some marketers may legitimately want to continue ad targeting of unsubscribed email contacts — the opt-out was channel-specific). However, the platform should surface a recommendation to enable exclusion sync as a best practice, and should log whether exclusion sync is enabled or disabled per integration for the individual visibility record.

---

## Individual Rights and Third-Party Data

When an individual requests deletion from a marketer (see [Right to Delete](../Identity/right-to-delete.md)):

- The platform can delete the contact record from Symposia.
- The platform **cannot** delete the individual's data from Facebook, Google, or other ad platforms where their hashed identifier has already been synced.
- The platform can remove the individual from any future sync operations (by adding them to the integration exclusion list).
- The individual is informed of this limitation: "We have removed you from [Marketer]'s contact list and from future syncs to Facebook and Google. Data already synced to those platforms is outside our control — to request deletion there, use Facebook's Data Deletion tools / Google's account controls."

This limitation must be disclosed clearly in the portal and in the deletion flow. It is one of the reasons that [consent is required before a contact can be included in an ad platform sync](#consent-requirement) — if consent was never given, no sync should have occurred, and there is nothing to disclose.

---

## Open Questions

1. **Match key expansion**: Should the platform support syncing additional match keys beyond email and phone (e.g., mobile advertising IDs — IDFA/GAID — to the ad platforms)? This improves match rates but requires device-level data that may not be available for all contacts.

2. **Conversion API integrations**: Ad platforms now offer server-side conversion APIs (Meta CAPI, Google Enhanced Conversions) that allow marketers to send purchase and event signals directly from the server rather than relying on browser pixels. These are related but distinct from audience sync — they require their own spec.

3. **Real-time vs. batch sync**: Current design assumes batch sync (scheduled, e.g., daily). Some ad platforms support real-time or near-real-time audience membership updates. Should the platform support real-time sync, and does that change the logging/visibility model?

4. **Audience lifecycle management**: When a contact is removed from a segment that feeds an ad platform sync, should they be automatically removed from the custom audience at the ad platform? This "negative sync" capability is supported by most platforms but has operational complexity.
