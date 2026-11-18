# ADR 0020: Station payables and dual control

**Status:** Accepted, 2026-11-19
**Source:** `docs/architecture/implementation-plan.md` §7 B6; roadmap delta A1, A3, A4; `docs/architecture/b6-station-finance-contract.md`

## Context
Since B3 every completed fueling credits `station:{id}:payable`, but nothing paid a station, nothing said where to pay it, and nothing could undo a fueling. B6 adds payouts, bank accounts and reversals. These are the paths where money leaves the platform, and changing a bank account is the highest-value fraud path (delta A3). The owner has not yet set the payout threshold or the hold window.

## Decision
1. **A station payable gets what a wallet has (ADR 0017).** `station_payables` keeps a cached balance, changed only under its row lock and in the same transaction as the ledger entry that moves it. The entry records `balance_after_piastres`, and nightly reconciliation checks payables as it checks wallets. Unlike a wallet, a payable may go **below zero**: a fueling reversed after the station was paid leaves the station owing the platform, recovered from its next payouts.
2. **Lock order:** wallet, then payable. Every station-finance decision is taken under the payable lock, after any wallet lock, so two payouts recorded at the same moment are decided one after the other.
3. **What may be paid** is `balance − held − pending payouts`, read in one statement (`PayableBook`).
4. **Dual control is enforced twice.** For a bank account change, a payout above the threshold and a reversal, one person asks and a different person decides. The service refuses a self-decision (`SELF_APPROVAL_NOT_ALLOWED`), and a CHECK in V9 refuses a row whose decider equals its requester, whatever the code does. Super Admin and Ops hold both halves of each pair, so a small team can cover for each other. The control is the person, not the role.
5. **A new bank account is held** for a configurable window (72 hours by default) before any payout may go to it, the station's first account included. The account number is encrypted at rest and returned only as its last four characters.
6. **One transfer pays once:** a payout's bank reference is normalised and unique among pending and posted payouts. A posting's source is the payout id, so it cannot post twice.
7. **A reversal is a mirror, never an edit.** The fueling row stays immutable (V7_1). Approval posts a `REVERSAL` transaction naming the original (`reverses_transaction_id`). The ledger allows one reversal per transaction; the wallet and receivable get back what each paid.
8. **Portals read, Operations writes.** Row-level security lets an operator read its own payables, entries, posted payouts, masked bank accounts and approved reversals, and lets a company read its approved reversals. All writes are platform-scope only.
9. **Station managers need a second factor**, because they read bank details.

## Consequences
- Policy values are configuration (`zawed.finance.*`), so the owner can set them without code.
- A reversal does not give back limit usage or clear a cooldown; that is an open product decision.
- Tests: `StationFinanceTests`, `BankAccountHoldTests`, and rows in `AuthorizationMatrixTests` for every endpoint.
