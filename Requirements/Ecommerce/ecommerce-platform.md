# E-Commerce Platform

**Status: Phase 2 — Stub**

This document captures the intent and scope of Symposia's hosted e-commerce capability. Full requirements will be defined when this is prioritized.

---

## Intent

If a marketer's e-commerce system runs entirely within Symposia, no product catalog ingestion is needed — the `catalog.products` and `catalog.categories` tables (see [Product & Category Catalog](../MarketingData/product-catalog.md)) are the system of record. The e-commerce platform writes to them directly.

This is the deepest integration tier: Symposia as not just the marketing layer but the transactional commerce layer. Storefront, cart, checkout, payment, order management, and fulfillment signals all live within the platform, giving the marketing automation layer zero-latency access to commerce events.

---

## Anticipated Scope (TBD)

- **Storefront**: hosted product listing pages, category pages, product detail pages, search
- **Cart and checkout**: managed cart state, checkout flow, address collection, payment processing (via [Payment and Stablecoin Integration](../Platform/payment-and-stablecoin-integration.md))
- **Order management**: order records, fulfillment status, returns and refunds
- **Inventory management**: stock levels per SKU, warehouse/location, restock triggers
- **Pricing engine**: price rules, promotional pricing, coupon codes, tiered pricing
- **Product management UI**: marketer-facing product/category editor (the `catalog` schema as the source of truth)

---

## Why It's a Large One

The e-commerce platform is a distinct product surface of comparable scope to the marketing automation stack. It introduces:

- Transactional data requirements (ACID-critical order writes) alongside analytical queries
- PCI-DSS scope for payment handling
- Complex inventory state management across potentially many fulfillment locations
- Real-time stock reservation during checkout (prevent oversell)
- A customer-facing storefront (separate from the marketer-facing dashboard)
- Return/refund flows that must propagate back to the marketing layer (refund events, loyalty impact)

This is explicitly deferred to a later phase. The Product & Category Catalog, Tracking System, and Journey Engine are all designed to work with externally hosted e-commerce systems in Phase 1 — Symposia does not need to be the commerce layer to deliver marketing automation value.

---

## Phase 1 Bridge

Marketers running e-commerce on Shopify, WooCommerce, BigCommerce, Magento, or a custom platform integrate via:
- [Product catalog ingestion](../MarketingData/product-catalog.md#ingestion-methods) (file feeds, ETL, API, scraper)
- [JS tracker](../Tracking/tracking-architecture.md) on their storefront for behavioral events (`product_viewed`, `cart_add`, `purchase`)
- [Conversion API integration](../Integrations/ad-platform-integrations.md) for server-side purchase signals to ad platforms

These integrations give the Symposia marketing layer full visibility into commerce behavior without requiring the marketer to move their storefront.
