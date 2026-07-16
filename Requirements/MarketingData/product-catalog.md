# Product & Category Catalog

## Overview

The product and category catalog is the marketer's structured database of their merchandise. It lives in the marketer's provisioned Postgres (the same instance as the [Contact Database](./contact-database.md)) under a dedicated `catalog` schema, and is the data foundation for:

- **Journey triggers**: Back in Stock and Price Drop automations detect changes in the catalog and emit events to the Journey engine
- **Affinity intelligence**: Category and brand affinity scores in the [Symposia Data Cloud](../DataCloud/symposia-data-cloud.md) are seeded by cross-contact product interaction signals mapped back to catalog taxonomy
- **Personalization**: Product recommendations, dynamic product blocks in email templates, and "you left this behind" cart context all reference the catalog
- **Ad platform feeds**: Google Shopping, Meta Dynamic Product Ads, and similar channels consume structured product feeds — the catalog is the source of truth for these exports (see [Ad Platform Integrations](../Integrations/ad-platform-integrations.md))
- **Segmentation**: Product-level and category-level segment filters (e.g., "contacts who purchased from category X in last 90 days") join against catalog taxonomy

---

## Schema

The catalog schema lives alongside `marketing` in the marketer's Postgres instance.

### Products

```sql
CREATE TABLE catalog.products (
  product_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           UUID NOT NULL,
  external_id         TEXT NOT NULL,           -- marketer's SKU or product ID (from their system of record)
  name                TEXT NOT NULL,
  description         TEXT,
  brand               TEXT,
  url                 TEXT,
  image_url           TEXT,                    -- primary image
  additional_images   JSONB DEFAULT '[]',      -- [{ "url": "...", "alt": "..." }]
  category_id         UUID REFERENCES catalog.categories(category_id),
  category_path       TEXT[],                  -- breadcrumb e.g. ['Electronics', 'Video Games', 'Consoles']
  price               NUMERIC(10,2),           -- regular/list price
  sale_price          NUMERIC(10,2),           -- active sale price (null if not on sale)
  currency            TEXT NOT NULL DEFAULT 'USD',
  availability        TEXT NOT NULL DEFAULT 'in_stock',
                      -- in_stock | out_of_stock | preorder | discontinued | backorder
  condition           TEXT NOT NULL DEFAULT 'new',   -- new | refurbished | used
  attributes          JSONB DEFAULT '{}',      -- flexible: size, color, material, weight, etc.
  tags                TEXT[] DEFAULT '{}',
  gtin                TEXT,                    -- barcode (UPC, EAN, ISBN) for ad platform feeds
  mpn                 TEXT,                    -- manufacturer part number
  is_active           BOOLEAN NOT NULL DEFAULT TRUE,
  created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_ingested_at    TIMESTAMPTZ,             -- last time this product was seen in any feed/scrape/API call

  UNIQUE (tenant_id, external_id)
);

CREATE INDEX ON catalog.products (tenant_id, availability);
CREATE INDEX ON catalog.products (tenant_id, category_id);
CREATE INDEX ON catalog.products (tenant_id, brand);
CREATE INDEX ON catalog.products (tenant_id, is_active, updated_at DESC);
```

### Categories

```sql
CREATE TABLE catalog.categories (
  category_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           UUID NOT NULL,
  external_id         TEXT,                    -- marketer's category ID
  name                TEXT NOT NULL,
  parent_id           UUID REFERENCES catalog.categories(category_id),
  path                TEXT[],                  -- full path from root: ['Electronics', 'Video Games']
  iab_category        TEXT,                    -- IAB Tech Lab taxonomy mapping (for Data Cloud affinity)
  url                 TEXT,
  image_url           TEXT,
  is_active           BOOLEAN NOT NULL DEFAULT TRUE,
  created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),

  UNIQUE (tenant_id, external_id)
);

CREATE INDEX ON catalog.categories (tenant_id, parent_id);
```

`iab_category` maps the marketer's own category to the IAB Tech Lab content taxonomy. This enables cross-marketer category affinity scoring in the Data Cloud — "Video Games" at one marketer maps to the same IAB category as "Gaming" at another.

### Price History

Append-only log of price changes per product. Used to detect price drops and to build price sensitivity signals.

```sql
CREATE TABLE catalog.product_price_history (
  history_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           UUID NOT NULL,
  product_id          UUID NOT NULL REFERENCES catalog.products(product_id),
  price               NUMERIC(10,2),
  sale_price          NUMERIC(10,2),
  recorded_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX ON catalog.product_price_history (tenant_id, product_id, recorded_at DESC);
```

### Availability History

Append-only log of availability changes per product. Used to detect back-in-stock transitions.

```sql
CREATE TABLE catalog.product_availability_history (
  history_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           UUID NOT NULL,
  product_id          UUID NOT NULL REFERENCES catalog.products(product_id),
  availability        TEXT NOT NULL,
  recorded_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX ON catalog.product_availability_history (tenant_id, product_id, recorded_at DESC);
```

---

## Change Detection and Event Emission

When a product's price or availability changes — via any ingestion method — the catalog processor compares the incoming value against the current value in `catalog.products` and emits events to NATS before writing the update.

| Change Detected | Event Emitted | NATS Subject |
|---|---|---|
| `availability` changed from any unavailable state to `in_stock` | `catalog.product_back_in_stock` | `sym.{tenant_id}.catalog.product_back_in_stock` |
| `price` or `sale_price` decreased | `catalog.product_price_dropped` | `sym.{tenant_id}.catalog.product_price_dropped` |
| `availability` changed to `out_of_stock` or `discontinued` | `catalog.product_went_out_of_stock` | `sym.{tenant_id}.catalog.product_went_out_of_stock` |
| New product created | `catalog.product_created` | `sym.{tenant_id}.catalog.product_created` |

These events are the triggers for the [Back in Stock](../UseCases/marketing-automation-use-cases.md#3-back-in-stock) and [Price Drop](../UseCases/marketing-automation-use-cases.md#4-price-drop) Journey use cases. The Journey engine subscribes to these NATS subjects.

Event payload example:
```json
{
  "event_id": "uuid-v7",
  "event_type": "catalog.product_price_dropped",
  "tenant_id": "uuid",
  "product_id": "uuid",
  "external_id": "SKU-99",
  "name": "Trail Running Shoes",
  "url": "https://malamute.com/products/trail-running-shoes",
  "image_url": "https://cdn.malamute.com/img/trail-shoes.jpg",
  "previous_price": 129.99,
  "new_price": 89.99,
  "previous_sale_price": null,
  "new_sale_price": 89.99,
  "currency": "USD",
  "discount_pct": 30.8,
  "occurred_at": "2026-07-02T10:00:00Z"
}
```

A history row is written to `product_price_history` or `product_availability_history` on every change, regardless of whether an event is emitted.

---

## Ingestion Methods

Seven methods are supported for loading product and category data into the catalog. They are not mutually exclusive — a marketer may use the API for real-time price/availability updates while using a daily file feed for full catalog refreshes.

---

### 1. File Transfer — S3 / Azure Blob Storage

The marketer uploads a product feed file to a designated folder in their Symposia blob storage bucket. The catalog feed processor watches for new or updated files in the `catalog-feeds/` prefix and processes them on arrival (triggered by the `blob.created` or `blob.updated` event from the blob storage notification system).

**Supported feed formats:**

| Format | Notes |
|---|---|
| Google Merchant Center TSV | Standard format; most ecomm platforms can export this natively |
| Google Merchant Center XML | RSS 2.0 + Google namespace |
| JSON Lines (`.jsonl`) | One product object per line; Symposia's native format |
| CSV (custom column mapping) | Marketer maps columns to catalog fields via the UI or API at setup time |

**File drop path:**
```
{tenant-bucket}/catalog-feeds/products/feed.tsv
{tenant-bucket}/catalog-feeds/categories/categories.csv
```

Full catalog refresh vs. delta update is inferred from a file-level header or naming convention:
- `feed_full_*.tsv` — replace the full catalog (products not in the file are marked inactive)
- `feed_delta_*.tsv` — upsert only; products absent from the file are untouched

See [Blob Storage — Event Notifications](../BlobStorage/blob-event-notifications.md) for how `blob.created` triggers downstream processing.

---

### 2. File Transfer — SFTP

For marketers whose existing systems only support file transfer via SFTP (common in enterprise retail), Symposia exposes an SFTP interface to blob storage. The SFTP server maps to the marketer's designated blob bucket — files written via SFTP appear as blobs and trigger the same feed processing pipeline as Method 1.

This is a blob storage interface feature, not a separate storage system. See [Storage Interfaces — SFTP](../BlobStorage/storage-interfaces.md#sftp-interface) for the full spec.

**Behavior from the marketer's perspective:** configure their system to SFTP product files to `catalog-feeds/products/` on the Symposia SFTP endpoint using their blob storage credentials. No other changes needed — the rest of the pipeline is identical to S3/Azure file drop.

---

### 3. Direct Postgres Write

Because the catalog lives in the marketer's own Postgres instance, marketers with the technical capability can write directly to `catalog.products` and `catalog.categories` using a dedicated database user scoped to the `catalog` schema.

This is the lowest-latency method and the right choice for marketers whose ecomm backend already runs against Symposia's Postgres (e.g., a headless storefront with Symposia as the database layer).

**Note**: Direct writes bypass the catalog processor's change detection. Marketers writing directly are responsible for manually calling `POST /catalog/products/{id}/notify-change` after writes that should trigger Journey events (price drop, back in stock), OR the marketer enables the catalog change trigger — a Postgres trigger function that publishes NATS events on `catalog.products` row updates (opt-in; has a small per-write overhead).

---

### 4. ETL Service

The Symposia ETL service connects to an external data source on a configured schedule, extracts product data, transforms it to the catalog schema, and loads it into the marketer's `catalog` tables.

**Status: Stub** — the ETL service is not yet specced. See [Todo — ETL & Data Loading](../Todo.md#etl--data-loading).

Intended source connectors include: Shopify, WooCommerce, Magento, BigCommerce, SAP Commerce, external Postgres/MySQL, Google Merchant Center account sync, Snowflake/BigQuery. Connector model is expected to be Airbyte-compatible.

When the ETL service delivers rows to the catalog tables, it routes through the catalog processor (not direct writes), so change detection and event emission work automatically.

---

### 5. Symposia Product API

The Product API is the right choice for real-time, event-driven updates — a price change in the marketer's system triggers an immediate API call to Symposia, which in turn detects the change and fires the Journey trigger within seconds.

The API also supports full catalog management (create, update, deactivate products). See [Product API](#product-api) below.

The API routes through the catalog processor — change detection and event emission are automatic.

---

### 6. Website Scraper

**Status: Stub**

For marketers who cannot or do not want to expose an API, database connection, or file export, Symposia can crawl their website to discover and extract product data.

**How it works:**
1. Marketer provides their website domain and (optionally) a `sitemap.xml` URL or URL pattern for product pages
2. The scraper fetches the sitemap to discover product page URLs
3. Each product page is fetched and parsed for structured data — preference order: JSON-LD (`schema.org/Product`), microdata, Open Graph, heuristic HTML extraction
4. Extracted product fields (name, price, availability, images, description, brand) are normalized to the catalog schema
5. Categories are inferred from the URL path or breadcrumb markup
6. Scrapes run on a daily schedule; on-demand scrapes can be triggered via API

**Extracted from `schema.org/Product` JSON-LD:**
- `name`, `description`, `brand`, `sku`, `gtin`, `mpn`
- `offers.price`, `offers.priceCurrency`, `offers.availability`
- `image`, `url`
- `category` (if present)

**Limitations:**
- JavaScript-heavy SPAs require headless browser rendering (slower, higher cost)
- No access to inventory levels beyond availability signals (in stock / out of stock)
- Price changes only detected on the next scheduled scrape — not real-time
- Requires `robots.txt` compliance; Symposia's scraper user-agent must be whitelisted

Full scraper spec TBD, including: headless rendering policy, scrape frequency tiers by plan, retry/backoff on rate limiting, handling of login-gated product pages.

---

### 7. Hosted E-Commerce (Phase 2)

**Status: Stub — Phase 2**

If the marketer's e-commerce system runs entirely within Symposia — storefronts, product catalog management, checkout, order management — then no ingestion is needed. The `catalog.products` and `catalog.categories` tables ARE the system of record, written to directly by the Symposia e-commerce layer.

See [E-Commerce Platform](../Ecommerce/ecommerce-platform.md) for the Phase 2 scope stub.

---

## Feed Formats Supported

| Format | Method(s) | Notes |
|---|---|---|
| Google Merchant Center TSV | File (S3, SFTP) | Columns: `id`, `title`, `description`, `link`, `image_link`, `availability`, `price`, `brand`, `gtin`, `mpn`, `google_product_category` |
| Google Merchant Center XML | File (S3, SFTP) | RSS 2.0 + `g:` namespace |
| JSON Lines | File (S3, SFTP), ETL | `{ "external_id": "...", "name": "...", ... }` per line |
| Custom CSV | File (S3, SFTP), ETL | Column mapping configured at setup time |
| Shopify Product Export | ETL connector | Native Shopify API; maps to catalog schema |
| WooCommerce Product Export | ETL connector | REST API or DB connector |
| schema.org/Product | Scraper | JSON-LD or microdata on product pages |

---

## Product API

```
# Products
GET    /catalog/products                        List products (filterable by category, brand, availability)
POST   /catalog/products                        Create product
GET    /catalog/products/{id}                   Get product
PUT    /catalog/products/{id}                   Full update
PATCH  /catalog/products/{id}                   Partial update (e.g. price or availability only)
DELETE /catalog/products/{id}                   Deactivate product (sets is_active = false)
POST   /catalog/products/{id}/notify-change     Manually trigger change detection (for direct Postgres writers)

# Categories
GET    /catalog/categories                      List categories (tree structure)
POST   /catalog/categories                      Create category
GET    /catalog/categories/{id}                 Get category
PUT    /catalog/categories/{id}                 Update category
DELETE /catalog/categories/{id}                 Deactivate category

# Feed ingestion
POST   /catalog/feeds/submit                    Trigger processing of a file already in blob storage
  { "blob_path": "catalog-feeds/products/feed.tsv", "format": "gmc_tsv", "mode": "full" }
GET    /catalog/feeds/{job_id}                  Feed processing job status and summary

# Scraper
POST   /catalog/scrape                          Trigger an on-demand scrape
  { "start_url": "https://example.com/sitemap.xml" }
GET    /catalog/scrape/{job_id}                 Scrape job status

# Price and availability history
GET    /catalog/products/{id}/price-history     Full price history log
GET    /catalog/products/{id}/availability-history  Full availability history log
```

---

## Relationship to Other Systems

| System | How the Catalog Connects |
|---|---|
| [Contact Database](./contact-database.md) | `marketing.contact_events` records product interactions (`product_viewed`, `cart_add`, `purchase`) that reference `catalog.products.external_id` |
| [Journeys](../Journeys/journeys.md) | `catalog.product_back_in_stock` and `catalog.product_price_dropped` events are Journey trigger events |
| [Segmentation Engine](./segmentation-engine.md) | Segments can filter on contact event history joined to catalog (e.g., "purchased from category X") |
| [Symposia Data Cloud](../DataCloud/symposia-data-cloud.md) | Category and brand affinity signals are built from product interaction data mapped to `catalog.categories.iab_category` |
| [Personalization Engine](../Messaging/personalization-engine.md) | Journey context (cart contents, browsed product) references catalog for product images, names, prices in email templates |
| [Ad Platform Integrations](../Integrations/ad-platform-integrations.md) | Product feed exports for Google Shopping and Meta DPA pull from `catalog.products` |
| [Blob Storage](../BlobStorage/storage-interfaces.md) | File-based ingestion methods (S3, Azure, SFTP) land files in blob storage before the feed processor picks them up |
| [Queue / Pub-Sub](../Platform/queue-and-pubsub.md) | All catalog change events (`catalog.product_*`) published to NATS |
