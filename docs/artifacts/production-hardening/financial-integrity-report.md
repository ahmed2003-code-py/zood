# Financial integrity report

## Top-up workflow (ADR 0021)

```
company claims ─► PENDING ─(Ops verifies: received amount, account, reference, date, evidence)─► PENDING_APPROVAL
                     │                                                                       │
                     └─ company may cancel                     Ops Manager (≠ verifier) approves ─► CONFIRMED  (posts verified amount)
                                                               anyone with WALLET_CREDIT rejects ─► REJECTED
Ops records an unannounced transfer ──────────────────────────────────────────────────────────► PENDING_APPROVAL
```

| Rule | Where it is enforced |
|---|---|
| A claim moves nothing | No posting before `CONFIRMED`; CHECK `topup_requests_confirmed_has_posting` |
| The credited amount is the verified one | `TopUps.approve` posts `verified_amount_piastres` |
| The verifier never approves | Service (`SELF_APPROVAL_NOT_ALLOWED`) and CHECK `topup_requests_two_person` |
| The verifier and approver are the users acting | Guard trigger: `verified_by` and `decided_by` = `app_user_id()` |
| A verification is written once | Guard trigger |
| Only an Ops Manager or Super Admin approves | Permission `TOPUP_APPROVE` (matrix-tested) |
| A transfer credits once | Unique (`receiving_account`, `bank_reference_key`) among live rows; unique ledger source (`TOPUP`, id) |
| A rejected or cancelled top-up is never credited | Guard: decided rows never change; approve requires `PENDING_APPROVAL` |
| A repeated approval does not double-credit | Repeat answers `CONFIRMED`; the source key would refuse a second posting anyway |
| The company withdraws only unverified claims | RLS update policy (`status = 'PENDING'`) and service |

## Wallet adjustments
- **Who:** requested with a reason by `WALLET_CREDIT`, approved by a different `TOPUP_APPROVE` holder.
- **Posting:** `platform:adjustments` → wallet, source type `ADJUSTMENT`.
- **Credits only.** Taking money from a wallet is done by reversing the transaction that put it there (ADR 0020).
- **Protections:** CHECK constraints (two-person, cancelled by the requester, approved has a posting) and a guard trigger (acting user, immutable request).

## Ledger invariants (unchanged, re-verified by the suite)
- Every transaction balances (deferred constraint triggers); entries are append-only.
- One posting per source, and one reversal per transaction.
- Cached wallet and payable balances change only under their row lock, with `balance_after_piastres` on the entry; a wallet is ≥ 0.
- Offline shortfalls go to `company:{id}:receivable`, and the station is paid in full.
- Nightly reconciliation compares cached balances with the ledger.

### Normal balance by account kind

| Kind | Normal side | Increases with | Example |
|---|---|---|---|
| `PLATFORM_BANK` | debit (asset) | money arriving | top-up approved |
| `PLATFORM_ADJUSTMENT` | debit (contra) | credits granted without bank money | adjustment approved |
| `COMPANY_WALLET` | credit (liability to the company) | top-ups, adjustments, reversals | |
| `COMPANY_RECEIVABLE` | debit (asset) | offline fuel the wallet could not cover | |
| `STATION_PAYABLE` | credit (liability to the station) | fuelings | payouts and reversals debit it |

## Verified tests
`TopUpTests` (16 tests): the verified amount is credited, the verifier and plain Ops cannot approve, the database refuses single-step confirmation, a repeated approval credits once, racing approvals credit once, a rejected top-up is never credited, cancel rules, uniqueness per receiving account, replay of a recorded transfer, reference variants, and the statement.

`WalletAdjustmentTests` (4), `ReconciliationTests`, `LedgerTests`, `LedgerIsolationTests`, `StationFinanceTests`, `MigrationCompatibilityTests` (legacy confirmed top-ups stay final). Results: `testing-report.md`.

## Remaining financial risks
- **Independent review pending:** Fable has not reviewed this code.
- **Offline exposure is monitored, not capped** across devices (ADR 0025). A receivable ceiling needs an owner decision.
- **A wrong verified amount** is possible if both people misread the statement. The approver sees the claimed and verified amounts side by side. A discrepancy is audited (`claimedAmountPiastres`, `verifiedAmountPiastres` in the audit facts).
- **No automated bank statement import or matching** yet (V1.5 in the roadmap).
