# Static Website Hosting

## Scope Decision: Out of Scope for v1

Static website hosting — where a bucket is configured to serve its contents as a website at a custom domain, with index documents and error pages — is **explicitly out of scope for v1** of this platform.

This is a deliberate scoping decision, not an oversight. The rationale:

- The platform's primary value proposition is reliable, performant, decentralized blob storage with S3/Azure interface compatibility. Static website hosting is a derivative feature that builds on top of that.
- Static website hosting with custom domains requires TLS certificate provisioning (Let's Encrypt or equivalent), DNS management integration, and a routing layer that maps hostnames to buckets. This is non-trivial engineering that would delay the core storage platform.
- The S3 interface compatibility means that existing static site tooling (Netlify, Vercel, Cloudflare Pages, or a custom nginx/Caddy reverse proxy in front of S3) can already serve static sites backed by this platform's storage — the hosting layer just lives elsewhere.

---

## What This Means for Tenants

Tenants who want to serve static websites from this platform in v1 can:

1. **Use a CDN or reverse proxy**: Point Cloudflare, Fastly, or any CDN at the platform's S3-compatible endpoint. Configure the CDN to handle custom domains, TLS, and index/error document routing.
2. **Use a static site host that supports custom S3 endpoints**: Some static site hosting platforms allow you to bring your own S3-compatible storage backend.
3. **Configure public read access**: Enable `public_access: read` on the bucket so the CDN can fetch objects without credentials.

This pattern is already widely used and well-documented. The platform's S3 compatibility is the enabling feature; the hosting layer is external.

---

## Future Consideration

Static website hosting may be added in a future version if there is sufficient demand. If implemented, the requirements would include:

- Per-bucket website configuration: index document (e.g., `index.html`), error document (e.g., `404.html`).
- Custom domain mapping with automatic TLS certificate provisioning.
- Redirect rules (prefix redirects, condition-based redirects).
- A dedicated website endpoint URL distinct from the API endpoint.
- Integration with a CDN or edge layer for caching and performance.

A governance proposal and community feedback process would precede any implementation.
