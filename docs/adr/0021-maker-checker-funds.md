# ADR 0021: Maker-checker for money entering a wallet

**Status:** Accepted, 2026-11-19
**Source:** `docs/architecture/production-readiness-audit.md` F1 and F2; owner instruction of 2026-11-19 (verified amount, Ops Manager approval)

## Context
Before V12, confirming a top-up credited the amount the company typed, and one Ops user could also credit any wallet directly. ZAWED has no payment gateway: money arrives by bank transfer and someone must read the statement. A typed claim is not proof, and one person should never be able to create money in a wallet.

## Decision
1. **Three steps, two people.**
   - A company's request is a claim (`PENDING`).
   - An Ops user with `WALLET_CREDIT` verifies it against the statement: the amount that arrived, the receiving account, the reference as printed, the value date and an evidence reference (`PENDING_APPROVAL`).
   - A different user with `TOPUP_APPROVE` approves (`CONFIRMED`), which posts exactly the verified amount.
   - Ops may record a transfer that has no request; it starts at `PENDING_APPROVAL`.
2. **A new role, not a hierarchy.** `OPS_MANAGER` is Ops plus `TOPUP_APPROVE`. Super Admin also holds it. Plain Ops verifies but never approves.
3. **Wallet adjustments** (credits that are not bank transfers) have the same rule:
   - they live in their own table, `wallet_adjustments`, and require a reason;
   - they post against a new counter-account, `platform:adjustments`, so they never look like bank money;
   - they are credits only (money leaves a wallet only by reversing what put it there).
4. **Uniqueness per receiving account.** A bank reference is unique among live top-ups per ZAWED receiving account (`zawed.finance.receiving-accounts`), not across every bank.
5. **Defence in depth.**
   - The service refuses self-approval (`SELF_APPROVAL_NOT_ALLOWED`).
   - The database refuses it too: a CHECK constraint, plus a guard trigger requiring the verifier and decider to be the acting user (`app_user_id()`) and a verification to be written once.
6. **Idempotent approval.** Approving a confirmed top-up answers it unchanged. Concurrent approvals serialise on the row lock and the posting's unique source, so the money posts once.

## Alternatives
- **Keep the single-step confirmation with an amount field:** still one person, so rejected.
- **A threshold below which one person suffices:** simpler, but the owner asked for approval of every top-up. It can be added later as configuration.
- **A generic approvals table for every two-person action:** more abstraction than the four existing flows need.

## Consequences
- **API change:** `POST /ops/topups/{id}/confirm` is replaced by `/verify` and `/approve`, and `POST /ops/companies/{id}/wallet/credits` now records a transfer instead of posting. The Ops screens are updated in the same branch.
- **Migration:** rows confirmed before V12 are marked `legacy_single_step` and keep their history.
- **Concurrency:** lock order is top-up row, then wallet, so it cannot cycle with fueling (wallet only).
