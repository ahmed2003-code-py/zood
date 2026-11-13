# B3 contract: ledger and wallet

**Status:** implemented on `develop`, 2026-11-13 · **Schema:** `V6__ledger_and_wallets.sql` · **Module:** `apps/api/src/main/java/com/zawed/api/ledger`
**Builds on:** `b1-identity-contract.md` (sign-in, permissions, error envelope, pagination, audit), `b2-master-data-contract.md` (companies).
**Sources:** PRD §3, §4.1–4.3, §4.6, §4.7, §6.2–6.4, §8.3 · plan §3.4, §3.5, §7 B3 · ADR 0004, 0005, 0006, 0007, 0011, 0017.

## 1. Accounts and postings

Double-entry, integer piastres, append only. Every posting is one `ledger_transactions` row with at least two `ledger_entries` whose debits equal credits; the database checks this at commit and refuses any `UPDATE`, `DELETE` or `TRUNCATE` of either table for every role, the owner included. A correction is a new posting.

| Account | Code | Opened |
|---|---|---|
| Platform bank | `platform:bank` | V6 |
| Company wallet | `company:{companyId}:wallet` | when the company is onboarded (`CompanyCreated` event); V6 backfilled existing companies |
| Station payable | `station:{stationId}:payable` | on first use (B4 fueling) |

| Event | Source | Debit | Credit | Phase |
|---|---|---|---|---|
| Top-up confirmed or credited directly by Ops | `TOPUP` / top-up id | `platform:bank` | `company:{id}:wallet` | B3 |
| Billable fueling | `FUELING` / fueling id | `company:{id}:wallet` | `station:{id}:payable` | B4 |
| Station payout | `PAYOUT` / payout id | `station:{id}:payable` | `platform:bank` | B6 |
| Reversal | `REVERSAL` / reversal id | mirror | mirror | B6 |
| Manual correction (Super Admin, two-person rule) | `ADJUSTMENT` / adjustment id | either | either | B6; the B3 tests use it to debit wallets |

`(source_type, source_id)` is unique: a top-up, fueling or payout can never post twice. `ledger_transactions.posted_by` is the acting user (PRD §4.6 step 5) and `reference` the bank or receipt reference.

**Company wallet.** `company_wallets` holds the cached balance (`CHECK >= 0`), derived from the ledger and changed only under the row's `FOR UPDATE` lock, in the same transaction as the entry. Each wallet entry records `balance_after_piastres` (PRD §8.3 "resulting balance"). Entries carry a database sequence; for one wallet it is the exact order of its running balance because the entries are inserted under the wallet lock, so the statement orders and pages by it, not by id.

## 2. The `Ledger` API (what B4 and B6 call)

`com.zawed.api.ledger.Ledger`, injected like any bean, called **inside the caller's transaction** (`Propagation.MANDATORY`):

```java
Wallet lockWallet(UUID companyId);   // FOR UPDATE; 404 outside the caller's scope
Posted post(Posting posting);        // locks the wallets it touches, posts, moves the cached balances
```

`Posting(sourceType, sourceId, reference, description, poster, entries)`; `Entry.debit(account, piastres)` / `Entry.credit(...)`; `Account.platformBank()`, `Account.companyWallet(id)`, `Account.stationPayable(id)`; `Poster.of(principal)` or `Poster.SYSTEM`.

Rules a caller can rely on:

- Wallets are locked in ascending company id. A caller that locks the wallet first (plan §3.4 step 4: wallet, then tag) holds the same lock; `post` does not wait.
- A duplicate source answers `409 ALREADY_POSTED` without aborting the database transaction when the wallet lock serialised the two callers; a true race on the unique index also answers `409 ALREADY_POSTED` (the transaction is then rolled back by the exception).
- A wallet debit below zero answers `409 INSUFFICIENT_BALANCE` with `params.balancePiastres` and `params.requiredPiastres`. The database `CHECK` says the same.
- An unbalanced or single-entry `Posting` does not construct (`IllegalArgumentException`): a programming error, not a 4xx.
- `Posted.wallets()` says, per wallet, the balance before and after and whether the posting **crossed the low-balance threshold downwards**. When it did, the ledger writes the audit event `WALLET_LOW_BALANCE` and publishes `WalletLowBalance` synchronously in the same transaction.

**B4 fueling, step by step:** `BEGIN` → `ledger.lockWallet(companyId)` → lock the tag → PRD checks (balance from the locked wallet) → insert the fueling row → `ledger.post(FUELING, fuelingId, receiptRef, …, [debit wallet, credit station payable])` → cooldown, hard alerts → `COMMIT`. If the alerts module wants a hard low-balance alert, it listens to `WalletLowBalance` with `@EventListener` (same transaction); e-mail and push go through the outbox after commit.

**Offline sync (B5) and the non-negative wallet.** PRD §4.7 says an offline capture that pushes the true balance negative is still committed. The `CHECK (balance_piastres >= 0)` refuses that today. B5 must decide between (a) a migration that relaxes the check and an alert on a negative balance, or (b) posting the shortfall to a `company:{id}:overdraft` receivable account. Who bears that loss is an open business question (`docs/demo/overnight-decisions.md`).

## 3. Who can do what

| Permission | Roles | Covers |
|---|---|---|
| `COMPANY_READ` | Fleet Manager, Accountant | `GET /company/wallet`, `/company/wallet/statement`, `/company/topups` |
| `TOPUP_REQUEST` | Fleet Manager, Accountant | `POST /company/topups`, `POST /company/topups/{id}/cancel` |
| `OPS_READ` | Super Admin, Ops | `GET /ops/topups`, `/ops/topups/{id}`, `/ops/companies/{id}/wallet`, `/ops/companies/{id}/wallet/statement`, `/ops/reconciliation/runs`, `/ops/reconciliation/runs/{id}/findings` |
| `WALLET_CREDIT` | Super Admin, Ops | `POST /ops/topups/{id}/confirm`, `/ops/topups/{id}/reject`, `/ops/companies/{id}/wallet/credits` |
| `RECONCILIATION_RUN` | Super Admin | `POST /ops/reconciliation/runs` |

Why both company roles may request: PRD §4.6 names the "company finance team" as the party that notifies Operations; §6.2 lists "wallet top-up" among the support tickets the Fleet Manager opens; the Accountant's restriction (§3) is "any configuration action", and a request changes no configuration and moves no money. Only Operations credits (§3 "recharge corporate wallets"; §6.3 "Credit Wallet action", "Wallet Top-Up Processing"). **Owner to confirm** (see the B3 summary).

Station Manager sees none of it; station payables are B6.

## 4. Endpoints

All lists are paginated (`?cursor=&limit=`, newest first) and return `{items, nextCursor}`.

**Company portal** (`/api/v1/company`, scope = the signed-in user's company)

| Method | Path | Body | Success |
|---|---|---|---|
| GET | `/wallet` | — | `WalletResponse { companyId, balancePiastres, lowBalanceThresholdPiastres, lowBalance, updatedAt }` |
| GET | `/wallet/statement` | — | `Page<StatementEntryResponse>`; the cursor is the entry sequence |
| GET | `/topups?status=` | — | `Page<TopUpResponse>` |
| POST | `/topups` | `{ amountPiastres, bankReference, transferDate, note? }` | `201 TopUpResponse` (status `PENDING`) |
| POST | `/topups/{id}/cancel` | — | `200 TopUpResponse` (`CANCELLED`) |

**Ops** (`/api/v1/ops`)

| Method | Path | Body | Success |
|---|---|---|---|
| GET | `/topups?status=&companyId=` | — | `Page<TopUpResponse>` |
| GET | `/topups/{id}` | — | `TopUpResponse` |
| POST | `/topups/{id}/confirm` | `{ bankReference?, note? }` | `200 TopUpResponse` (`CONFIRMED`, `ledgerTransactionId` set); `bankReference` replaces the requested one when the statement shows a different reference |
| POST | `/topups/{id}/reject` | `{ reason }` | `200 TopUpResponse` (`REJECTED`) |
| GET | `/companies/{id}/wallet` | — | `WalletResponse` |
| GET | `/companies/{id}/wallet/statement` | — | `Page<StatementEntryResponse>` |
| POST | `/companies/{id}/wallet/credits` | `{ amountPiastres, bankReference, transferDate, note? }` | `201 TopUpResponse` (`CONFIRMED`, `requestedBy` null) |
| GET | `/reconciliation/runs` | — | `Page<ReconciliationRunResponse { id, trigger, triggeredBy, startedAt, finishedAt, walletsChecked, findingsCount }>` |
| POST | `/reconciliation/runs` | — | `201 ReconciliationRunResponse` |
| GET | `/reconciliation/runs/{id}/findings` | — | `Page<ReconciliationFindingResponse { id, runId, companyId, kind, cachedBalancePiastres, ledgerBalancePiastres, differencePiastres, createdAt }>` |

`StatementEntryResponse { id, postedAt, direction (CREDIT adds, DEBIT takes), amountPiastres, balanceAfterPiastres, sourceType, sourceId, reference, description }`.

`TopUpResponse { id, companyId, amountPiastres, bankReference, transferDate, note, status, requestedBy, requestedAt, decidedBy, decidedAt, decisionReason, ledgerTransactionId }`. `decisionReason` holds the rejection reason or the confirmation note.

## 5. Rules

**Top-ups.**
- Amount in piastres, `1 … 100,000,000,000` (1,000,000,000 EGP). `transferDate` not after today in Africa/Cairo. `bankReference` at most 64 characters.
- The reference is normalised (trimmed, upper case, spaces removed) and **belongs to at most one pending or confirmed request, platform-wide** (a unique index): the same transfer can never credit twice, whoever claims it and however it is spelled. A rejected or cancelled request frees its reference.
- A confirmed request is exactly one that posted (`CHECK`), and a posting can be referenced by one request only. The posting's source is the request id, so even a re-confirmed request cannot post again.
- Decisions lock the request row (`FOR UPDATE`) and flip it with `… where status = 'PENDING'`; a second decision answers `409 TOPUP_NOT_PENDING`.
- Row-level security lets a company insert only `PENDING` rows for itself and change them only to `CANCELLED`; confirming and rejecting are platform-only at the database level too.

**Wallet and low balance.**
- `lowBalance = balance < company.lowBalanceThresholdPiastres` (strictly below; a threshold of 0 never flags).
- `WALLET_LOW_BALANCE` is audited (and `WalletLowBalance` published) once per downward crossing, not on every posting below the line.
- The wallet is opened in the company's onboarding transaction by a synchronous listener on `CompanyCreated`; tenancy does not know the ledger exists. The reconciliation job reports a company without a wallet as `MISSING_WALLET`.

**Reconciliation.**
- `ledger_reconcile(...)`, a `SECURITY DEFINER` function, compares every `company_wallets.balance_piastres` with the sum of its entries in **one statement** (one snapshot, so a posting that commits meanwhile cannot show as a mismatch), records a run, its findings and an audit row, under `pg_try_advisory_xact_lock(hashtext('ledger_reconcile'))`. The job (`Reconciliation.nightly`, 02:30 Africa/Cairo) and the Super Admin's run-now call the same function; while another run holds the lock, run-now answers `409 RECONCILIATION_RUNNING` and the job logs and skips.
- Findings are append only. Resolution (who fixed what) is deferred to the cases module (B6).

**Audit** (same transaction, under the company's scope unless noted): `WALLET_CREATED`, `TOPUP_REQUESTED`, `TOPUP_CANCELLED`, `TOPUP_CONFIRMED` (with `transactionId`, `balanceAfterPiastres`, and `bankReferenceRequested` when corrected), `TOPUP_REJECTED`, `WALLET_CREDITED`, `WALLET_LOW_BALANCE`, `RECONCILIATION_RUN` (platform scope, actor null for the scheduled job).

## 6. Error codes added

| Code | Status | When |
|---|---|---|
| `ALREADY_POSTED` | 409 | A second posting of the same source |
| `INSUFFICIENT_BALANCE` | 409 | A wallet debit below zero; `params.balancePiastres`, `params.requiredPiastres` |
| `BANK_REFERENCE_IN_USE` | 409 | `field: "bankReference"`; a pending or confirmed top-up already carries it |
| `TOPUP_NOT_PENDING` | 409 | Confirm, reject or cancel after a decision; `params.status` |
| `INVALID_BANK_REFERENCE` | 400 | `field: "bankReference"`; blank after normalisation |
| `TRANSFER_DATE_IN_FUTURE` | 400 | `field: "transferDate"` |
| `RECONCILIATION_RUNNING` | 409 | Run-now while a run holds the lock |
| `INVALID_CURSOR` | 400 | A statement cursor that is not a sequence number |
| `NOT_FOUND` | 404 | A wallet, top-up or run outside the caller's scope or missing |

## 7. Database guarantees (tests in `apps/api/src/test/java/com/zawed/api/ledger`)

| Guarantee | Where | Test |
|---|---|---|
| Debits equal credits, at least two entries, checked at commit for every role | deferred constraint triggers `ledger_*_balanced` | `LedgerTests.theDatabaseRejectsAnUnbalancedTransactionForEveryRole` |
| Entries and transactions immutable for every role | no `UPDATE`/`DELETE` grant + `ledger_refuse_change` triggers (also `TRUNCATE`) | `LedgerTests.ledgerEntriesAndTransactionsCanNeverBeUpdatedOrDeleted` |
| One posting per source, also under a race | unique `(source_type, source_id)` + pre-check under the wallet lock | `LedgerTests.aDuplicateSourcePostsOnceEvenUnderARace` |
| Cached balance never negative; equals the ledger after parallel credits and debits | `CHECK`, row lock, running balance chain | `LedgerTests.parallelCreditsAndDebitsLeave…`, `aWalletNeverGoesBelowZero` |
| One bank reference credits once, also under a race | partial unique index on the normalised key | `TopUpTests.theSameBankReferenceNeverCreditsTwice`, `racingRequestsWithOneReferenceAcceptOne`, `racingConfirmationsCreditOnce` |
| An entry's company is its account's company | `ledger_entry_check_account` trigger | exercised by every posting |
| RLS: platform all, company own, operator none; company may only request and cancel | V6 policies | `LedgerIsolationTests` |
| Reconciliation finds a corrupted cache and nothing on a clean wallet; one run at a time; runs without a principal | `ledger_reconcile` | `ReconciliationTests` |
| Migrations and functions work for a non-superuser owner | V6 under `NonSuperuserOwnerTests` | `NonSuperuserOwnerTests` |

## 8. Not in B3

- Station payables, payouts and the station statement (B6). Reversals and manual adjustments as endpoints (B6, two-person rule).
- Resolving or annotating a reconciliation finding.
- E-mail or push on low balance (the alerts module, B4, listens to `WalletLowBalance`).
- Exports of the statement (B6 reporting).
