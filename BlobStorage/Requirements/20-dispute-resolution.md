# Dispute Resolution

## Overview

The platform involves three distinct classes of parties — tenants, node operators, and the platform itself — and disputes can arise between any combination of them. The on-chain record provides an objective source of truth for many disputes, but not all conflicts are resolvable by reading chain data. This file defines the process for handling disputes in a way that is fair, transparent, and efficient.

---

## Dispute Categories

### Tenant vs. Platform

| Dispute Type | Example |
|---|---|
| Billing dispute | Tenant believes they were charged incorrectly for storage or egress. |
| Data loss claim | Tenant believes data was permanently lost due to platform failure. |
| Wrongful account suspension | Tenant's account was suspended and they believe it was in error. |
| SLA breach claim | Tenant claims the platform failed to meet its stated availability guarantee. |
| Deletion dispute | Tenant claims a blob was not fully deleted from all replicas as required. |

### Node Operator vs. Platform

| Dispute Type | Example |
|---|---|
| Slash dispute | Node operator believes a stake slash was applied incorrectly or unfairly. |
| Tier dispute | Node operator believes their tier classification does not reflect actual performance. |
| Region verification dispute | Node operator believes their region claim was incorrectly rejected. |
| Reward calculation dispute | Node operator believes their epoch reward was calculated incorrectly. |

### Tenant vs. Node Operator

Direct disputes between tenants and individual node operators are rare by design — tenants interact with the platform abstraction, not individual nodes. If one arises (e.g., a node operator is identified as a cause of data loss), the dispute is handled as a Tenant vs. Platform dispute, with the platform seeking recourse from the node operator separately.

---

## Principles

- **On-chain data is authoritative**: For any dispute that can be resolved by examining chain state (slash events, reward distributions, region verification outcomes, blob metadata), the on-chain record is the source of truth. Neither party can unilaterally alter it.
- **Good faith first**: All disputes begin with an informal resolution attempt. Formal escalation is available but should not be the first step.
- **Timely resolution**: No dispute should remain unresolved beyond defined deadlines. Uncommunicated delays are not acceptable.
- **No retaliation**: Filing a dispute does not affect service, reward eligibility, or account standing while the dispute is pending.

---

## Tenant Dispute Process

### Step 1 — Self-Service (0–3 days)

- The tenant reviews their usage data, audit logs, and billing records via the observability tools.
- For billing disputes, a usage reconciliation tool allows the tenant to compare billed amounts against their own usage records epoch by epoch.
- Many billing disputes are resolved at this step — the tenant finds the discrepancy explanation in their own logs.

### Step 2 — Support Ticket (3–14 days)

- Tenant opens a support ticket describing the dispute with specifics: epoch number, blob keys, amounts, timestamps.
- A support agent reviews the on-chain record and internal logs and responds within 3 business days.
- For billing disputes: if an error is confirmed, a credit is applied to the tenant's account within 5 business days.
- For data loss claims: the platform provides an incident report including when the data loss occurred, which nodes were involved, what the replica count was at the time, and the outcome of the repair process.
- For SLA breach claims: the platform calculates whether a breach occurred per the SLA definition and applies any applicable service credits automatically.

### Step 3 — Formal Escalation (14–45 days)

If the tenant is unsatisfied with the Step 2 resolution:

- The tenant submits a formal dispute request with a written statement of their claim and the specific remedy sought.
- The platform's dispute review committee (minimum 3 members, including one who was not involved in the original support response) reviews the dispute.
- The committee may request additional evidence from both parties.
- A written decision is issued within 21 days of the escalation submission.
- If the decision finds in favor of the tenant, remedies may include: billing credits, token compensation, or (for data loss) compensation per the SLA terms.

### Step 4 — External Arbitration

- If the tenant remains unsatisfied after Step 3, they may pursue external arbitration under the rules defined in the Terms of Service (e.g., JAMS or AAA arbitration).
- Class action waivers and arbitration clauses are subject to legal review for enforceability in applicable jurisdictions.

---

## Node Operator Slash Dispute Process

Slash disputes are time-sensitive because a pending dispute should not prevent the node from operating.

### Step 1 — Automatic Evidence Package (immediate)

When a slash event is recorded on-chain, the platform automatically generates and delivers to the node operator:
- The specific trigger event and its timestamp.
- The metric data that caused the trigger (e.g., the proof-of-possession challenge that failed, the integrity check log).
- The chain transaction ID for the slash event.
- The amount slashed.
- The process for filing a dispute.

### Step 2 — Dispute Filing (within 7 days of slash)

- The node operator submits a dispute within 7 days of the slash event. Disputes filed after this window are not accepted.
- The dispute must include: the node operator's account of events, any supporting evidence (node logs, external network incident reports, hardware failure documentation), and the specific claim (e.g., "the slash was triggered by a network outage that also affected verifier nodes, making the challenge impossible to respond to").

### Step 3 — Review (within 14 days of filing)

- The platform reviews the on-chain evidence alongside the node operator's submission.
- The verifier nodes involved in the triggering event are queried for their own logs.
- If the slash is found to have been applied in error (e.g., a verifier-side bug, a network partition that affected the measurement), the slashed amount is returned to the node operator's stake.
- If the slash is upheld, the written decision explains the specific chain evidence that supports it.

### Step 4 — Governance Appeal (within 30 days of Step 3 decision)

- Node operators may escalate to an on-chain governance vote for disputes involving more than a defined minimum slash amount (e.g., more than 5% of stake).
- A governance proposal is submitted with both the platform's decision and the node operator's rebuttal.
- Token holders vote. The outcome is binding and executed on-chain automatically.
- This mechanism is intentionally expensive (requires governance participation) to deter frivolous appeals while protecting node operators from genuinely unfair slashes.

---

## Data Loss Compensation

If the platform is found to have caused permanent, unrecoverable data loss (blobs that cannot be retrieved by any means):

- The tenant is compensated at a rate defined in the SLA (see SLA requirements) per GB of confirmed lost data.
- Compensation is in the form of account credits or token transfer, at the tenant's election.
- Compensation is capped at the amount the tenant paid for storage of the lost data over the preceding 12 months.
- Compensation does not extend to consequential damages (downstream business losses, lost revenue from the tenant's customers, etc.) unless separately agreed in an enterprise contract.
- If the data loss was caused by a node operator's fault (confirmed via on-chain slash evidence), the platform pursues recovery from the node's slashed stake to offset the compensation paid to the tenant.
