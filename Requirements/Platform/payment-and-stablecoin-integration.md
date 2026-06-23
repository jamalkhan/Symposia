# Payment and Stablecoin Integration

## Overview

The native token is the primary unit of account for storage fees and node rewards. However, requiring tenants to hold and manage a volatile native token creates friction that blocks adoption — particularly for enterprises and developers who want to pay for storage in the same way they pay AWS or Azure. Stablecoin acceptance removes that friction without undermining the token economy: stablecoins are converted to the native token at settlement, so node runners always receive native token rewards.

This file defines which stablecoins are supported, how exchange rates are determined, what happens when swaps fail, and how the payment flow works end-to-end.

---

## Supported Stablecoins

At mainnet launch, the following stablecoins are supported for storage payment:

| Stablecoin | Network | Notes |
|---|---|---|
| **USDC** | Base (native) | Primary supported stablecoin. Bridged to L3 via OP Stack canonical bridge. |
| **USDT** | Base (bridged) | Secondary. Higher liquidity globally but carries Tether counterparty risk. |
| **DAI** | Base (bridged) | Decentralized stablecoin; included for users who prefer non-custodial stablecoins. |

Additional stablecoins are added via [Governance](../Blockchain/governance.md) (Tier 2 proposal). Removing a supported stablecoin requires a 90-day deprecation period with advance notice to affected tenants.

All stablecoin support is on **Base** (the L2 where the L3 settles). Tenants bridge stablecoins from Ethereum mainnet to Base using the canonical OP Stack bridge if needed. The platform does not operate a bridge — it uses existing infrastructure.

---

## Exchange Rate Oracle

Storage fees are denominated in the native token. When a tenant pays with a stablecoin, the platform must determine the current exchange rate (stablecoin-to-native-token) at settlement time.

### Oracle Design

- The platform uses a **time-weighted average price (TWAP)** oracle reading from the deepest liquidity pool for the native token on Base DEXes (initially Uniswap v3 on Base or Aerodrome).
- The TWAP window is **1 hour** (6 epochs of 10 minutes each). This smooths short-term price spikes that could advantage or disadvantage either the tenant or the network.
- Oracle reads are recorded on-chain at each epoch boundary so that any participant can independently verify the rate applied to their settlement.
- The oracle is not operated by the platform — it is a read from the public on-chain price feed. The platform does not have custody of oracle data.

### Oracle Fallback

If the primary oracle pool has insufficient liquidity (< $500,000 TVL) or the TWAP price deviates more than 20% from the secondary oracle (Chainlink price feed for the stablecoin/ETH pair × ETH/token), the settlement falls back to the secondary oracle. If both oracles are unavailable or report implausible data, stablecoin settlements are paused until data recovers. Tenants with stablecoin credits are not charged during the pause.

---

## Payment Flow

### Credit Purchase with Stablecoin

1. The tenant initiates a credit purchase, specifying the stablecoin amount and currency.
2. The platform quotes the equivalent number of native token credits at the current TWAP rate (displayed to the tenant before confirmation).
3. The tenant approves a token transfer from their wallet to the platform's settlement contract.
4. The settlement contract receives the stablecoin and immediately initiates a swap to the native token via the designated DEX pool on Base.
5. The received native tokens are deposited to the platform's credit reserve for the tenant's account.
6. The tenant's credit balance is updated atomically with the completion of the swap. Credits are denominated in the native token; the stablecoin amount is an input to the purchase, not the denomination of the credit.

### Credit Denomination

Credits are always held and spent in units of the native token, even when purchased with stablecoins. This means:
- If the tenant purchases credits with $100 USDC when 1 token = $0.10, they receive 1,000 token-credits.
- If the token price later rises to $0.20, those same 1,000 token-credits now represent $200 of purchasing power.
- Conversely, if the token price falls, the dollar value of remaining credits falls.

This behavior is equivalent to converting to the native token at purchase time and holding it. Tenants who want to avoid token price exposure should purchase credits in small amounts as needed rather than pre-purchasing large credit blocks.

### Invoice Display

API and dashboard usage displays are shown in both native token units and a USD equivalent at the current oracle rate, with a clear indication that USD values are estimates and actual cost in USD depends on token price at credit purchase time.

---

## Swap Execution

When a stablecoin credit purchase triggers a swap:

- The platform sends the stablecoin to the DEX pool with a maximum slippage tolerance of **1%**. If the pool cannot execute at this slippage tolerance (insufficient liquidity, high volatility), the swap reverts.
- On revert, the stablecoin remains in the settlement contract and the tenant's pending credit purchase is held in an `pending_swap` state.
- The tenant is alerted (via webhook or email) that the swap did not execute. They may:
  - Wait for the platform to retry the swap (automatic retry every 5 minutes for up to 1 hour).
  - Cancel the pending credit purchase and receive their stablecoin back via the settlement contract.
- After 1 hour without successful execution, the pending credit purchase is automatically cancelled and the stablecoin is returned to the tenant's wallet.

### Large Purchases

For credit purchases above $50,000 equivalent, the platform splits the swap into multiple tranches over multiple blocks to minimize price impact. The total credits are applied after all tranches complete. Large purchases may take up to 30 minutes to fully settle; the tenant can see the progress in their dashboard.

---

## Auto-Top-Up

Tenants may configure automatic credit top-up to avoid service interruption from low balances:

```json
{
  "auto_top_up": {
    "enabled": true,
    "trigger_threshold_days": 7,
    "top_up_amount_usdc": 500,
    "currency": "USDC",
    "max_monthly_spend": 5000
  }
}
```

When the tenant's estimated remaining credit balance drops below `trigger_threshold_days` of usage, the platform automatically initiates a credit purchase of `top_up_amount_usdc` using the configured stablecoin. The `max_monthly_spend` cap prevents runaway charges if usage unexpectedly spikes.

Auto-top-up requires the tenant to pre-approve a spending allowance on the settlement contract for the platform. The approval amount should be set to the maximum expected monthly spend. Tenants revoke the allowance at any time from their wallet or from the platform dashboard.

---

## Refunds and Credit Returns

- Credits are **non-refundable in fiat or stablecoin** once the stablecoin has been swapped to the native token. The swap is irreversible.
- Unused credits may be:
  - **Transferred to another account within the same tenant organization** (account-to-account transfer within a tenant).
  - **Burned by the tenant** if the account is being closed; burned credits are not compensated.
- In the event of platform-confirmed data loss or SLA breach, compensation is applied as a credit addition (see [SLA and Availability Guarantees](./sla-and-availability-guarantees.md) and [Dispute Resolution](../Legal/dispute-resolution.md)). Compensation credits are denominated in native tokens at the time of the resolution, not at the time of the original purchase.

---

## KYC and Payment Compliance

Credit purchases above $10,000 equivalent (per single transaction) trigger an enhanced KYC check for the purchasing account if not already completed. This is required by applicable anti-money-laundering regulations in most jurisdictions.

Accounts flagged for sanctions screening (see [Content Moderation and Legal Policy](../Legal/content-moderation-and-legal-policy.md)) are blocked from making credit purchases. Existing credits on a sanctioned account are frozen until the sanction status is resolved.

---

## Tax Reporting

- The platform does not provide tax advice.
- Tenants are responsible for understanding the tax treatment of stablecoin-to-token swaps, token-denominated service payments, and any token appreciation or depreciation in their own jurisdiction.
- The platform provides downloadable transaction records (stablecoin payment date, amount, swap rate, tokens received) for every credit purchase, suitable for providing to a tax advisor.
