# ADR 0025: Offline exposure across devices

**Status:** Accepted (monitoring), with a proposal for later · **Source:** audit F7

## Context
- **What exists:** an offline terminal decides against its snapshot. The snapshot allowance is at most 5,000 EGP per device, and the server holds payables past 10,000 EGP per device and company in 24 hours.
- **The gap:** a disconnected device cannot know what other disconnected devices spent, so no company-wide cap can be enforced offline. With *n* devices offline, the worst case is about *n* times the allowance.
- **Today's outcome:** the server records everything and turns any shortfall into a receivable.

## Decision (implemented)
- **Monitoring, not enforcement:** a company-wide alert, `COMPANY_OFFLINE_EXPOSURE`, raised once when a company's offline fuelings across all devices in 24 hours cross `zawed.sync.company-offline-alert-piastres` (default 2,000,000 piastres, 20,000 EGP). It never blocks or changes a fueling (`CompanyOfflineMonitoringTests`).
- **Index:** `fuelings_company_offline_idx` serves the 24-hour sum.

## Proposal (not implemented; owner decision)
1. **Receivable ceiling:** when a company's receivable exceeds `X`, new snapshots mark its wallet `SUSPENDED`, so offline is refused and online is unaffected. The debt is then bounded by `X` plus one snapshot window across all devices.
2. **Allocated budgets:** split a company's allowance across its recently active devices (allowance divided by the number of devices). This is tighter, but fragile when the device count is stale.

No global cap is claimed until one of these is approved and built.
