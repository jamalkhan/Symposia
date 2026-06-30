# Stakeholders and Personas

## Overview

This document defines the distinct parties that participate in the Symposia network and establishes the vocabulary used consistently across every other requirements document. Getting these definitions precise matters because the data ownership model (see [User Data Ownership](../Identity/user-data-ownership.md)) and the contact database's ownership rules (see [Contact Database](../MarketingData/contact-database.md)) depend entirely on which party is which.

There are four distinct parties:

---

## 1. Individual

A person who can be marketed to. An individual is a human being — a consumer, a prospect, a customer — who interacts with marketers' brands, websites, and email campaigns.

- An individual has a **Symposia identity** (a wallet-backed identity, see [User Data Ownership](../Identity/user-data-ownership.md)) that persists across every marketer they interact with on the network.
- An individual owns the data that **identifies** them: name, email address(es), phone number(s), physical address, and similar directly-identifying attributes.
- An individual is not a customer of Symposia in the commercial sense — they don't pay for the platform. They are the subject the platform exists to protect and empower. Every other party's access to an individual's data flows through permission the individual grants.
- One individual may be known to many marketers, and — critically — different marketers may know **different facts** about the same individual (see [Contact Database — One Individual, Multiple Marketer Views](../MarketingData/contact-database.md#one-individual-multiple-marketer-views)).

## 2. Marketer

A business or organization that uses the Symposia platform to reach and understand individuals. A marketer is a distinct legal/commercial entity — **Walmart and Hyatt are two different marketers**, even though both run on the same platform, and even though both might hold a record for the same individual. A marketer is always tenant-scoped: each marketer operates within their own tenant boundary (their own contact database, their own sending domains, their own segments and campaigns).

- A marketer is granted **permission** by individuals to use their identifying data for specific purposes (email marketing, web tracking, etc.) — see the permission model in [User Data Ownership](../Identity/user-data-ownership.md).
- A marketer **owns** the data they create or derive about an individual through their own business activity: order history, purchase behavior, support tickets, loyalty tier, internally-computed scores. This is distinct from — and does not require permission in the same way as — the individual's identifying data, because it's the marketer's own business record of the relationship. (It is still subject to the individual's right to request anonymization — see below.)
- A marketer may also **purchase or license** derived data products from appbuilders (e.g., a propensity-to-churn score computed by a third-party model).
- "Marketer" in every other requirements document refers to this entity — a business, not a person. The actual humans operating the marketer's account (the people who log into the platform, build segments, send campaigns) are the marketer's staff/operators, not a separate persona for the purposes of data ownership rules.

## 3. AppBuilder

An organization or developer that builds applications, models, or services that run on top of the Symposia platform and that **create new data about individuals** — data that did not come directly from the individual and is not simply the marketer's own first-party business record.

Examples:
- A company that builds a brand-affinity or purchase-propensity ML model trained across aggregated, permissioned signals, and licenses access to the resulting scores to marketers.
- A developer offering a "best time to email this person" prediction service.
- A third-party loyalty/rewards app that computes engagement tiers and sells that classification to multiple marketers.

- An appbuilder is a platform-level participant, not scoped to a single marketer's tenant the way a marketer's own staff are. An appbuilder's product may be consumed by many marketers (that's the business model — compute something valuable once, license it many times).
- An appbuilder **owns** the derived data/models they create, in the same sense a marketer owns their own derived data — see [General Ownership Rule](#general-ownership-rule) below.
- An appbuilder is bound by the same anonymization/pseudonymization obligation as a marketer when an individual exercises their deletion rights (see [Right to Delete](../Identity/right-to-delete.md)).
- Symposia itself, when it builds platform-level features that derive data about individuals (e.g., a network-wide fraud score), acts as an appbuilder under this model — the platform operator does not get a privileged ownership exemption.

## 4. Symposia (the Platform / Foundation)

The protocol, infrastructure, and governing foundation. Symposia operates the network, defines and enforces the rules in these requirements documents, and provides the infrastructure (blob storage, compute, messaging, identity) that marketers and appbuilders build on.

- Symposia does not own individuals' identifying data and does not have standing access to decrypted data held by marketers or appbuilders (see the no-backdoors guarantee in [Security](./security.md)).
- Symposia does act as an appbuilder when it ships platform-native features that derive data about individuals (see above) — in that capacity it follows appbuilder rules, not platform-operator exemptions.
- Symposia is the enforcer of the ownership model: the permission system, the deletion/anonymization pipeline, and the audit trail are platform infrastructure that marketers and appbuilders operate within, not optional conventions they can bypass.

---

## General Ownership Rule

This is the rule of thumb referenced throughout the Identity and MarketingData documents:

> **Data that identifies an individual is owned by the individual.** Name, email, phone, address, and similar directly-identifying attributes are the individual's. Individuals control what is shared with which marketers, and can delete this data outright.
>
> **Data that is *created* about an individual is owned by whoever created it** — a marketer (e.g., order history, purchase behavior) or an appbuilder (e.g., a licensed ML score). Creators may use, retain, and sell/license this data subject to the platform's rules. When an individual exercises their right to delete, this created data is **not necessarily erased** — it is **anonymized or pseudonymized** so it can no longer be tied back to the individual, while its aggregate/analytical value to its owner is preserved where possible.

See [Contact Database](../MarketingData/contact-database.md) for how this rule is implemented in the data model, and [Right to Delete](../Identity/right-to-delete.md) for how the anonymization obligation is fulfilled mechanically.

---

## Relationship Diagram

```
                    ┌─────────────────────┐
                    │      Individual      │  owns: identifying data
                    │   (Symposia wallet   │  controls: permission grants
                    │      identity)       │  rights: access, delete, portability
                    └──────────┬───────────┘
                               │ grants permission to use identifying data
                               │ (email_marketing, web_tracking, data_read, ...)
              ┌────────────────┼────────────────┐
              ▼                                 ▼
     ┌─────────────────┐               ┌─────────────────┐
     │     Marketer A   │               │     Marketer B   │   owns: data they create
     │   (e.g. Walmart) │               │   (e.g. Hyatt)   │   (orders, scores, tags)
     └────────┬─────────┘               └────────┬─────────┘
              │ may license derived data from              │
              ▼                                             ▼
                    ┌─────────────────────────┐
                    │       AppBuilder         │   owns: data/models they create
                    │ (e.g. propensity model   │   may license/sell to marketers
                    │   vendor, loyalty app)   │
                    └─────────────────────────┘

                    ┌─────────────────────────┐
                    │   Symposia (Platform)    │   enforces the rules above;
                    │                           │   acts as an appbuilder when it
                    │                           │   creates derived data itself
                    └─────────────────────────┘
```

---

## Why This Distinction Matters

Without this three-way split (individual-owned identity data / marketer-or-appbuilder-owned derived data / platform-enforced rules), "users own their data" collapses into a slogan that can't survive contact with how a real business operates. A marketer needs to retain their own order history and behavioral analytics — that's their business record, and forcing full deletion of it on every consumer request would make the platform commercially unusable for marketers. But an individual's identifying information — the thing that makes "this order history" attributable to "this specific person" — is unambiguously theirs to control and revoke.

The anonymization/pseudonymization mechanism is what reconciles these: the marketer keeps the analytical shape of their data (e.g., "a customer in this cohort bought X, Y, Z over 18 months") while losing the ability to know who that customer was. This is the same pattern privacy law generally expects (see the open TODO on jurisdictional anonymization standards in [Todo.md](../Todo.md)).
