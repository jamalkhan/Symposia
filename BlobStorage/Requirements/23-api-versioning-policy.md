# API Versioning Policy

## Overview

The platform's API will evolve. New features will be added, existing behaviors will need correction, and some endpoints will eventually be retired. Without a clear versioning policy, every change risks breaking tenant integrations — and a platform that breaks integrations without warning is not one that enterprises will trust with critical data.

This file defines how API versions are managed, how breaking changes are handled, and how long tenants can rely on any given version remaining available.

---

## API Surface Areas

The platform exposes three distinct API surface areas, each with different versioning considerations:

| Surface | Versioning Approach | Notes |
|---|---|---|
| **S3-Compatible Interface** | S3 protocol version, not the platform's own | The platform tracks the S3 API spec. Breaking changes here are driven by the S3 spec, not the platform. The platform documents which S3 API version it targets. |
| **Azure Blob–Compatible Interface** | Azure Blob API version header (`x-ms-version`) | Same as S3 — the platform tracks Azure's versioning convention. |
| **Platform-Native API** | Explicit version in URL path (`/v1/`, `/v2/`) | New features unique to this platform (region assignment, tier selection, event subscriptions, usage queries, etc.) use explicit path versioning. |

---

## Platform-Native API Versioning

### Version Format

Platform-native API versions use a major version number in the URL path:

```
https://api.example.com/v1/subscriptions
https://api.example.com/v2/subscriptions
```

Minor additions (new optional fields, new endpoints) do not increment the major version and are considered non-breaking. Tenants should build integrations to ignore unknown fields in responses.

### What Constitutes a Breaking Change

Breaking changes require a new major version:

- Removing an endpoint.
- Renaming an endpoint.
- Removing a required or optional field from a request or response.
- Changing the type or format of an existing field (e.g., string to integer, changing a date format).
- Changing the meaning of an existing field.
- Changing authentication behavior on an existing endpoint.
- Changing HTTP methods on an existing endpoint (e.g., GET to POST).
- Adding a new **required** field to a request.

Non-breaking changes (no new version required):

- Adding a new endpoint.
- Adding a new **optional** field to a request.
- Adding a new field to a response (tenants must tolerate unknown fields).
- Adding a new value to an enum (tenants must tolerate unknown enum values gracefully).
- Bug fixes that correct behavior that was clearly wrong per the documentation.
- Performance improvements with no behavior change.

### Deprecation Process

When a new major version is released:

1. **Announcement**: The previous version is marked deprecated. Deprecation is announced on the status page, in the changelog, via email to all tenants who have made API calls to the deprecated version in the past 90 days, and in response headers (`Deprecation: true`, `Sunset: <date>`).

2. **Minimum support period**: Deprecated API versions are supported for a **minimum of 12 months** after the announcement date. Enterprise tenants with contracts may negotiate longer support windows.

3. **Sunset date**: A specific sunset date is published at the time of deprecation announcement. The date is at least 12 months out and is not moved earlier once announced.

4. **Sunset warning headers**: During the 90 days before sunset, API responses on the deprecated version include a `Sunset` header with the date, giving automated monitoring systems a machine-readable signal.

5. **Sunset**: After the sunset date, the deprecated version returns `410 Gone` for all requests. The error response includes a link to the migration guide.

### Support Commitment

| Version State | Support |
|---|---|
| **Current** | Full support; bugs fixed, security patches applied. |
| **Deprecated** | Security patches only; no new features; bugs that are security issues are fixed, behavioral bugs are not. |
| **Sunset** | No support; returns 410. |

At any given time, there will be no more than two major versions in active support (current + one deprecated).

---

## S3 and Azure Compatibility Versioning

### S3

- The platform targets a documented S3 API version and publishes which version that is.
- When AWS updates the S3 API, the platform evaluates new features for inclusion. New S3 features are added in minor releases.
- S3 deprecations (if AWS deprecates an S3 API feature) are handled with the same 12-month notice period as platform-native API deprecations.

### Azure Blob

- Requests to the Azure-compatible interface may include the `x-ms-version` header (as Azure's own API requires). The platform supports a documented range of Azure API versions.
- Tenants using newer Azure SDK versions that send a newer `x-ms-version` value than the platform supports receive a clear error indicating the maximum supported version.

---

## Changelog

A public changelog is maintained and updated with every release:

- Every non-breaking addition is documented.
- Every deprecation is announced with the sunset date.
- Every breaking change (new major version) includes a migration guide: what changed, why, and a before/after example for every breaking change.

The changelog is available at a stable URL and via RSS/Atom feed.

---

## Client Guidance

Tenants building integrations are advised to:

- Subscribe to the changelog RSS feed to receive notification of all changes.
- Build integrations that tolerate unknown fields in responses without erroring.
- Build integrations that tolerate unknown enum values (e.g., a new event type in notifications) without erroring.
- Pin to a specific API version in production integrations; test against the new version in a staging environment before upgrading.
- Monitor for `Deprecation` and `Sunset` headers in API responses as machine-readable deprecation signals.
