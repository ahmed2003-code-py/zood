# Onboarding a pilot company and station

The fleet side, step by step (company, readiness, vehicles, bulk import, funding the wallet), is in [fleet-onboarding-guide.md](../artifacts/production-hardening/fleet-onboarding-guide.md). This runbook is the checklist around it, including the station side.

## Before day one: people and decisions

- [ ] At least one **Ops** user and one **Ops Manager**. Both enrol an authenticator at first sign-in. The Ops Manager is the second person for top-ups and adjustments, and cannot approve their own requests.
- [ ] The owner's open decisions that affect the pilot, recorded:
  - the offline receivable ceiling;
  - whether the commercial registration must be unique;
  - whether the plate category is part of a plate's identity;
  - the payout threshold.
- [ ] ZAWED's receiving bank accounts are configured (`zawed.finance.receiving-accounts`).

## Fleet company

1. Create the company, its departments and its fueling policy (guide §1).
2. Check readiness (guide §2).
3. Add vehicles: one by one, or by bulk import (guide §3–4).
4. Fund the wallet: verify the transfer, then a different person approves (guide §6).
5. Create the company's users (fleet manager, accountant). Each signs in once.

## Station operator and stations

1. Create the operator and its stations, and set the prices for the pilot fuel types.
2. Register the station's bank account. Changing a bank account later needs a second person, and payouts are held while it is pending (B6).
3. Register each POS device. Give the activation code to the person who installs the terminal; it is shown once.
4. Create the agents (worker id and PIN) and the station manager user, who signs in with a second factor.
5. Activate each terminal, then do one test fueling with a test vehicle. Check that it reaches the company portal, the station statement and the ledger.

## Isolation check (every new company)

- Sign in as the new company's user. You see only your own vehicles, fuelings and wallet.
- Signed in as the company, open a URL with another company's vehicle id. The answer is "not found", never the data.

## After go-live

- Watch the nightly reconciliation logs for the first week.
- Review sync conflicts and alerts daily (Ops → alerts, sync monitor).
- Take a backup before every deploy until point-in-time recovery is on ([backup-and-restore.md](backup-and-restore.md)).
