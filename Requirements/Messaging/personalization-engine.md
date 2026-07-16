# Personalization Engine

## Overview

The personalization engine renders email content tailored to each individual recipient at send time. Rather than sending a static message to thousands of people, marketers compose a template with dynamic expressions that the engine evaluates against each recipient's contact data — producing a unique rendered message per person.

**Liquid is the first templating engine implemented, not the only one the architecture supports.** The personalization engine is built around a pluggable templating abstraction (see [Templating Engine Abstraction](#templating-engine-abstraction) below) so that additional engines can be added over time — for marketers migrating from platforms with a different templating convention (Handlebars, Jinja-style, MJML-based systems), or for specialized rendering needs the Liquid implementation doesn't cover well. Liquid is the default and the only engine required for v1.

The scripting language for the default engine is **Liquid** (originally by Shopify, now open-source). Liquid is the most widely adopted template language in the martech industry, is safe (no arbitrary code execution), and is already familiar to marketers who have used Klaviyo, Shopify Email, HubSpot, or Braze.

---

## Why Liquid

- **Sandboxed**: Liquid has no file I/O, no network access, no arbitrary code execution. A template author cannot escape the rendering context.
- **Industry standard**: Marketers coming from Klaviyo, Shopify, HubSpot, or Braze already know it. Zero learning curve.
- **Expressive enough**: Supports variables, filters, conditionals (`if`/`unless`), loops (`for`), and basic math — sufficient for personalization without becoming a general-purpose language.
- **Multi-language .NET implementations**: `Fluid` (MIT license) is a high-performance, spec-compliant Liquid implementation for .NET/C#, making it a natural fit with the existing C# codebase.

---

## Templating Engine Abstraction

The platform defines a .NET interface that any templating engine implementation must satisfy. This keeps Liquid (and the `Fluid` library specifically) from being baked into the core sending pipeline — the delivery pipeline, personalization preview API, and template storage model are all engine-agnostic.

```csharp
public interface ITemplateEngine
{
    string EngineId { get; }                  // "liquid", "handlebars", "jinja", etc.

    Task<TemplateRenderResult> RenderAsync(
        string templateSource,
        IMergeContext mergeContext,
        TemplateRenderOptions options,
        CancellationToken cancellationToken);

    TemplateValidationResult Validate(string templateSource);
}
```

Each template record stores an `engine_id` alongside its source. The rendering pipeline resolves the configured engine for that template via a registry (`ITemplateEngineRegistry`) and dispatches to it. The merge context (contact data, campaign metadata, platform variables) is engine-neutral — built once per render and handed to whichever engine is configured.

Requirements any engine implementation must meet to be registered:
- **Sandboxed execution** — no filesystem, network, or environment access from within a template, matching the safety constraints already required of Liquid (see [Render Safety](#render-safety)).
- **Bounded execution** — must respect the 100ms render time / 1MB output size limits, enforced by the engine wrapper if the underlying library doesn't enforce it natively.
- **Deterministic output** — same template + same merge context always produces the same rendered output (required for preview/test-send parity with production sends).

This is a plugin point, not a per-tenant configuration surface in v1 — additional engines are added by the platform, not uploaded by tenants. Whether tenant-supplied custom engines become a feature is an open question for later phases.

---

## Template Variables

When a message is rendered for a recipient, the engine is given a **merge context** — a data object containing everything the template may reference. The context is assembled from:

1. **Contact data** — fields from the recipient's contact record in the marketer's [Contact Database](../MarketingData/contact-database.md).
2. **Campaign/broadcast metadata** — the send this message belongs to.
3. **Platform-provided variables** — unsubscribe URL, preference center URL, etc. (always available, cannot be overridden by contact data).
4. **Custom properties** — any arbitrary key-value pairs the marketer passes at send time.

### Standard Context Variables

```liquid
{{ contact.first_name }}           → "Jamal"
{{ contact.last_name }}            → "Khan"
{{ contact.email }}                → "jamal@example.com"
{{ contact.phone }}                → "+1-555-123-4567"
{{ contact.properties.company }}   → any custom property
{{ contact.properties.plan_tier }} → "Professional"

{{ campaign.name }}                → "June Newsletter"
{{ campaign.sent_at | date: "%B %e, %Y" }}  → "June 30, 2026"

{{ sender.name }}                  → "Malamute Adventures"   (from selected sender profile)
{{ sender.email }}                 → "hello@malamute.com"    (profile from_email)
{{ sender.address }}               → "123 Trail Rd, Denver CO 80201"  (profile postal_address)
{{ sender.reply_to }}              → optional reply-to from profile
# Multi-sender: each Campaign/send picks a sender_profile_id; see outbound-email-delivery.md

{{ unsubscribe_url }}              → one-click unsubscribe link (always injected)
{{ preferences_url }}              → preference center link
{{ view_in_browser_url }}          → hosted version of this email
{{ tracking_pixel_url }}           → 1×1 tracking image (injected automatically, not in template)
```

### Filters

Liquid filters transform values. Standard Liquid filters are available plus martech-specific additions:

| Filter | Example | Output |
|---|---|---|
| `upcase` | `{{ contact.first_name \| upcase }}` | `JAMAL` |
| `downcase` | `{{ contact.email \| downcase }}` | `jamal@example.com` |
| `capitalize` | `{{ contact.first_name \| capitalize }}` | `Jamal` |
| `date` | `{{ contact.created_at \| date: "%B %Y" }}` | `June 2026` |
| `default` | `{{ contact.first_name \| default: "Friend" }}` | `Friend` (if first_name is nil) |
| `truncate` | `{{ contact.bio \| truncate: 100 }}` | First 100 chars + `…` |
| `strip_html` | `{{ content \| strip_html }}` | Removes HTML tags |
| `escape` | `{{ user_input \| escape }}` | HTML-encodes special chars |
| `currency` | `{{ order.total \| currency: "USD" }}` | `$49.99` |
| `pluralize` | `{{ count \| pluralize: "item", "items" }}` | `3 items` |
| `encode_uri` | `{{ contact.email \| encode_uri }}` | URL-encodes for use in links |

### Conditionals

```liquid
{% if contact.first_name %}
  Hi {{ contact.first_name }},
{% else %}
  Hi there,
{% endif %}

{% if contact.properties.plan_tier == "Pro" %}
  <p>As a Pro member, you have access to...</p>
{% elsif contact.properties.plan_tier == "Starter" %}
  <p>Upgrade to Pro to unlock...</p>
{% endif %}

{% unless contact.properties.opted_in_sms %}
  <p>Want texts instead? <a href="{{ sms_signup_url }}">Sign up for SMS</a></p>
{% endunless %}
```

### Loops

Useful for rendering dynamic product lists, event lists, or recommendation sets passed in as custom properties:

```liquid
{% if contact.properties.recommended_products %}
  {% for product in contact.properties.recommended_products limit:3 %}
    <div class="product">
      <img src="{{ product.image_url }}" alt="{{ product.name }}">
      <a href="{{ product.url | encode_uri }}">{{ product.name }}</a>
      <p>{{ product.price | currency: "USD" }}</p>
    </div>
  {% endfor %}
{% endif %}
```

---

## Dynamic Content Blocks

Beyond variable interpolation, the personalization engine supports **dynamic content blocks** — entire sections of the email that are shown or hidden based on contact properties. This is distinct from Liquid conditionals in the template source: dynamic content blocks can be configured visually in the email editor without writing Liquid directly.

Under the hood, dynamic content blocks compile to Liquid conditionals. A marketer building a block in the visual editor sees:

- **Block name**: "Women's Products"
- **Condition**: Show when `contact.properties.gender == "female"`
- **Content**: (drag-and-drop email content)

Renders as:
```liquid
{% if contact.properties.gender == "female" %}
  [women's products HTML block]
{% endif %}
```

Dynamic content blocks may also be defined as **variants** — one of N mutually exclusive blocks shown depending on the condition, with a default fallback:

```liquid
{% case contact.properties.segment %}
  {% when "high_value" %}   [high-value customer content]
  {% when "at_risk" %}      [re-engagement content]
  {% else %}                [default content]
{% endcase %}
```

---

## Rendering Pipeline

The personalization engine runs at send time, not at template creation time. For each recipient in the send queue:

1. **Fetch merge context**: Load the recipient's contact record, campaign metadata, and any custom properties provided at send time.
2. **Compile template** (if not cached): Parse the Liquid template into an AST. Templates are cached in memory by template ID + version hash; re-parsing only happens on template update.
3. **Render**: Evaluate the template against the merge context. Rendering is strictly sandboxed — no I/O, no external calls, maximum execution time of 100ms per render.
4. **Post-process**:
   - Inject `{{ unsubscribe_url }}` into the footer if not present in the rendered HTML.
   - Replace `{{ view_in_browser_url }}` with a hosted URL pointing at the rendered message stored in blob storage.
   - Replace click-tracked links with redirect URLs (see [Tracking Architecture](../Tracking/tracking-architecture.md)).
   - Inject the tracking pixel into the HTML body.
5. **Output**: rendered subject line, HTML body, plain-text body (auto-generated from HTML if not separately defined).

### Render Safety

Templates must not be able to:
- Access the filesystem, environment variables, or any server-side resource.
- Make HTTP requests or reference external URLs at render time (URLs in templates are data, not active fetches).
- Consume unbounded memory or CPU. Templates that exceed 100ms render time or 1MB output size are rejected with an error.
- Access data from other contacts or other tenants. The merge context is strictly scoped to the current recipient.

The `Fluid` library's sandboxing model (allowlist of accessible properties) enforces this at the language level. The platform configures Fluid with an explicit allowlist of allowed properties from the contact object.

### Render Errors

When a template renders with missing or null variables that have no `| default:` fallback, the platform:
- For preview/test sends: returns the error with the line number, highlighting the problematic expression.
- For production sends: renders the variable as an empty string and logs the error. The message is still sent. This prevents a data gap from blocking the send.

Catastrophic errors (template parse failure, infinite loop, timeout) abort the entire send for that recipient and mark the message as `failed` in the queue.

---

## Previewing and Testing

Before sending, marketers need to see how a message will render for a specific contact.

### Preview API

```
POST /marketing/templates/{template-id}/preview

{
  "contact_id": "con_01abc",           // use a real contact's data
  "contact_override": {                // or override specific fields
    "first_name": "Jamal",
    "properties": { "plan_tier": "Pro" }
  }
}

Response:
{
  "rendered_subject": "Jamal, your June update is here",
  "rendered_html": "<!DOCTYPE html>...",
  "rendered_text": "Jamal, your June update...",
  "render_time_ms": 12,
  "warnings": ["{{ contact.phone }} was empty — rendered as empty string"]
}
```

### Send Test

```
POST /marketing/templates/{template-id}/send-test

{
  "to": "jamal@example.com",
  "contact_id": "con_01abc"   // render with this contact's data
}
```

Sends a rendered copy to the specified address. The message includes a visible test banner: `[TEST — not a real send]`.

### Seed List

A seed list is a set of real email addresses (internal QA inboxes, typically) that automatically receive a copy of every production send. Used for QA monitoring and inbox rendering checks. Seed list members are not suppression-checked (they are expected to receive everything).

```
GET  /marketing/sending-domains/{domain-id}/seed-list
POST /marketing/sending-domains/{domain-id}/seed-list
DELETE /marketing/sending-domains/{domain-id}/seed-list/{address}
```

---

## Plain-Text Generation

Every marketing email must include a plain-text alternative (MIME multipart). Some recipients prefer plain text; some spam filters score multi-part emails (HTML + text) more favorably.

If the marketer provides a separate plain-text template, it is rendered through the same Liquid engine using the same merge context.

If no plain-text template is provided, the platform auto-generates plain text from the rendered HTML:
- Strip all HTML tags.
- Convert `<a href="...">link text</a>` to `link text (URL)`.
- Convert `<br>` and block-level elements to newlines.
- Collapse multiple blank lines.
- The auto-generated plain text is not perfect but is sufficient for most cases.

---

## Template Management API

```
GET    /marketing/templates                      List templates
POST   /marketing/templates                      Create template
GET    /marketing/templates/{id}                 Get template
PUT    /marketing/templates/{id}                 Update template (creates new version)
DELETE /marketing/templates/{id}                 Archive template
GET    /marketing/templates/{id}/versions        List versions
GET    /marketing/templates/{id}/versions/{v}    Get specific version
POST   /marketing/templates/{id}/preview         Preview render
POST   /marketing/templates/{id}/send-test       Send test email
```

Template object:
```json
{
  "template_id": "tmpl_01abc",
  "name": "June Newsletter",
  "type": "marketing",
  "subject_liquid": "{{ contact.first_name | default: 'Friend' }}, your June update",
  "html_liquid": "<!DOCTYPE html>...[Liquid template]...",
  "text_liquid": null,
  "created_at": "2026-06-01T00:00:00Z",
  "updated_at": "2026-06-15T00:00:00Z",
  "version": 3,
  "created_by": "account-id"
}
```
