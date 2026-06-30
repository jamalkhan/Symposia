# Integrations Overview

## What an Integration Is

An integration is a marketer-activated connection between the Symposia platform and a third-party service. Integrations allow marketers to sync contact data, audiences, or event signals to external platforms — ad networks, CRMs, ESPs, data warehouses, and others.

Integrations are distinct from Symposia's own delivery channels (email, SMS, push). They represent **data egress** — contact data or behavioral signals leaving the Symposia platform and entering a third-party system that Symposia does not control. This has direct implications for individual data rights: once data has been synced to a third-party platform, Symposia cannot retrieve or delete it from that platform. What Symposia can do is maintain a complete, auditable record of what was sent, when, and to whom.

See also: [Delivery Channels Roadmap](../Messaging/outbound-email-delivery.md#delivery-channels-roadmap) for the distinction between integrations (data egress to external systems) and future delivery channels (sending messages via external ESPs, social platforms, CRMs — which is addressed separately).

---

## Integration Lifecycle

### Activation

When a marketer enables an integration, the platform records:

| Field | Description |
|---|---|
| `integration_type` | Identifier for the integration (e.g., `facebook_custom_audiences`, `google_customer_match`) |
| `marketer_tenant_id` | Which marketer activated it |
| `activated_at` | Timestamp |
| `activated_by` | Marketer staff account that performed the activation |
| `permissions_granted` | Which Symposia permissions the integration was granted (e.g., `contact_read`, `contact_sync`, `event_stream`) |
| `configuration_snapshot` | Non-sensitive summary of the configuration (e.g., which audience lists are being synced; no credentials stored here) |

### Sync Activity

Each sync operation (scheduled or manual) is logged:

| Field | Description |
|---|---|
| `sync_id` | Unique identifier for this sync run |
| `integration_type` | Which integration |
| `marketer_tenant_id` | Which marketer |
| `synced_at` | Timestamp of the sync |
| `contact_count` | Number of contacts included in this sync (aggregate — no individual contact IDs logged here) |
| `sync_type` | `full_refresh` or `delta` |
| `status` | `success`, `partial`, `failed` |

The platform does **not** log which specific contacts were included in each sync — only the aggregate count. This is intentional: the goal is to give individuals visibility into the fact and scale of the sync without building a per-contact egress ledger (which would itself become a privacy-sensitive dataset).

### Deactivation

When a marketer deactivates an integration, the platform records:

| Field | Description |
|---|---|
| `deactivated_at` | Timestamp |
| `deactivated_by` | Marketer staff account that performed the deactivation |
| `reason` (optional) | Marketer-provided reason |

Deactivation stops future syncs. It does not delete data already synced to the third-party platform — that is outside Symposia's control. The individual is shown the deactivation date and is informed that previously synced data may still exist at the third-party platform (see [Individual Visibility](#individual-visibility) below).

---

## Individual Visibility

The aggregate integration log is surfaced to individuals in the [Symposia Profile Portal](../Identity/user-profile-visibility.md). Individuals can see, for each marketer they are connected to:

- Which integrations the marketer has activated
- The date the integration was activated
- Aggregate sync volume over time (e.g., "7,543,123 contacts synced between 7/1/2026 and today")
- Whether the integration is currently active or was deactivated (and when)

**Example disclosure:**

> **Marketer: Walmart**
>
> | Integration | Status | Activated | Contacts Synced | Deactivated |
> |---|---|---|---|---|
> | Facebook Custom Audiences | Active | July 1, 2026 | 7,543,123 (ongoing) | — |
> | Google Customer Match | Inactive | July 1, 2026 | 7,543,123 (Jul–Sep 2026) | September 1, 2026 |

**What the platform cannot show**: what Facebook or Google holds about the individual, or whether a specific individual was included in a given sync. The sync log is aggregate — Symposia knows the count and the period, but does not maintain a per-contact record of which third-party platforms each contact's data was sent to. The individual is informed of this limitation in the portal.

**What the platform recommends**: individuals who want to know what Facebook or Google holds about them should use those platforms' own data access tools (Facebook's "Off-Facebook Activity" tool, Google's "My Ad Center," etc.). The portal surfaces links to these tools alongside the integration disclosure.

---

## Platform Policy: Integration Transparency as a Requirement

Marketers are required to disclose integrations accurately. Attempting to route data to third-party platforms in ways that bypass the integration logging system (e.g., building a custom export job that manually exports contact data and uploads it to Facebook Ads outside the platform integration) is a violation of the platform's acceptable use policy (see [Terms of Service and Acceptable Use](../Legal/terms-of-service-and-acceptable-use.md)).

This requirement exists because individual visibility into data flows is only meaningful if the log is complete. A platform that lets marketers route data outside the logging system while claiming "we show you where your data goes" is not actually delivering on that promise.

Enforcement mechanisms:
- Integration logging is built into the platform's data egress layer — the natural path for syncing contacts to an ad platform goes through the integration framework
- Out-of-band data egress (manual CSV downloads followed by third-party upload) is detectable via audit log patterns and is flagged for review
- Penalty for confirmed bypass: consistent with the [Terms of Service](../Legal/terms-of-service-and-acceptable-use.md) policy established for identifier misclassification — financial penalty, platform suspension, or referral to regulators depending on severity

---

## Consent and Legal Basis for Third-Party Syncs

Syncing contact data to a third-party ad platform requires an appropriate legal basis under GDPR and equivalent laws. The platform requires marketers to specify the legal basis for each integration at activation time:

| Legal Basis | Description | Applicability to Ad Platform Sync |
|---|---|---|
| `consent` | Individual has explicitly consented to their data being shared with this specific category of platform (ad targeting) | Strongest; required under GDPR for behavioral targeting |
| `legitimate_interests` | Marketer asserts a legitimate interest in the sync (e.g., lookalike audience building from existing customer list) | Accepted in some jurisdictions for customer-list matching; not accepted for general behavioral tracking |
| `contract` | Data sharing is necessary to fulfill a contract with the individual | Rarely applicable to ad platform sync |

The platform enforces a **minimum consent tier** for ad platform syncs: a contact must have at minimum granted `web_tracking_brand` permission (or its equivalent explicit consent to ad targeting) to be eligible for inclusion in an ad platform sync. Contacts without this permission are automatically excluded from sync operations, regardless of the marketer's legal basis selection.

This is a platform-enforced constraint, not a marketer-configurable policy.

---

## Integration Categories (Stub)

The following integration categories are planned. Each will have its own detailed requirements document as it is specced:

| Category | Examples | Status | Doc |
|---|---|---|---|
| **Ad Platforms** | Facebook/Meta Custom Audiences, Google Customer Match, TikTok Custom Audiences, LinkedIn Matched Audiences | Stub | [ad-platform-integrations.md](./ad-platform-integrations.md) |
| **CRM Sync** | Salesforce, HubSpot, Microsoft Dynamics | Not started | — |
| **ESP Sync** | Salesforce Marketing Cloud (SFMC), Braze, Klaviyo | Not started | — |
| **Data Warehouse** | Snowflake, BigQuery, Redshift | Not started | — |
| **CDP** | Segment, mParticle | Not started | — |
| **Webhooks** | Generic outbound webhook (marketer-defined endpoint) | Not started | — |

See [Todo.md](../Todo.md) for prioritization.
