# Concurrency report

All concurrency tests run on real PostgreSQL 18 (embedded) through the HTTP API, with separate connections (`support/Concurrently` starts the requests together behind a barrier). They assert database state (rows, ledger sums, cached balances), not only HTTP codes.

## Lock order (verified in code)

```
fueling:            wallet (FOR UPDATE) → vehicle (FOR SHARE) → tag → station payable (inside the posting)
top-up approve:     top-up row (FOR UPDATE) → wallet → [posting]
adjustment approve: adjustment row (FOR UPDATE) → wallet → [posting]
reversal approve:   reversal row → wallet(s) → payable(s)   (ledger.reverse sorts wallets, then payables)
payout:             payout row → payable
vehicle status:     vehicle (FOR UPDATE) → its tags and limits
tag issue/status:   vehicle (FOR UPDATE) → tag
import row:         plate unique index (insert) → vehicle row → limits → import row
```

The money paths always take wallets before payables, and request rows before wallets. A fueling takes no request row, so no cycle can form. `platform:bank` and `platform:adjustments` have no cached balance and are never locked, so unrelated companies never wait on each other: the wallet lock is per company.

## Races and protections

| Operation | Shared state | Invariant | Protection | Idempotency | Test |
|---|---|---|---|---|---|
| Duplicate fueling (same `clientTxnId`) | fuelings | one fueling | unique `client_txn_id`, payload-bound (tag and volume) | replay returns the stored outcome | `FuelingTests.oneClientTxnIdRacedOnTwoCompaniesRecordsOneFueling` |
| Fuelings sharing a wallet | wallet | balance ≥ 0, limits hold | wallet row lock serialises the company | — | `FuelingTests` (limit and balance cases), `LedgerTests.aDuplicateSourcePostsOnceEvenUnderARace` |
| Top-up approved twice or concurrently | top-up row, wallet | credited once | row lock, conditional flip, unique ledger source | repeat answers `CONFIRMED` unchanged | `TopUpTests.racingApprovalsByDifferentManagersCreditOnce`, `aRepeatedApprovalCreditsOnce` |
| Two requests with one reference | top-up index | one live top-up per reference and account | partial unique index | — | `TopUpTests.racingRequestsWithOneReferenceAcceptOne` |
| Recording the same transfer twice | top-up index | one record | `ON CONFLICT DO NOTHING`, payload compare | same payload answers 200 with the first record | `TopUpTests.recordingTheSameTransferTwiceAnswersTheFirstRecord` |
| Adjustment approved concurrently | adjustment row, wallet | posts once | row lock, conditional flip, unique source | repeat answers `APPROVED` | `WalletAdjustmentTests.racingApprovalsPostOnce` |
| Adjustment request retried | adjustments | one per key | `idempotency_keys` primary key in the same transaction | same key and body answer the same resource | `WalletAdjustmentTests.anIdempotencyKeyMakesARetrySafeAndRefusesADifferentBody` (4 racing requests) |
| Company create retried or double-clicked | companies, wallets | one company, one wallet | `idempotency_keys` | same as above | `CompanyIdempotencyTests.racingRequestsWithOneKeyCreateOneCompanyAndOneWallet` |
| Two registrations of one plate | plate index | one vehicle in service | partial unique `vehicles_plate_key` | — | `FleetIdentityTests.racingRegistrationsOfOnePlateCreateOneVehicle` |
| Two tags onto one vehicle | vehicle, tags | ≤ 1 active tag | vehicle lock, partial unique index | — | `FleetTests.twoTagsRacingOntoOneVehicleLeaveOneActive` |
| One UID onto two vehicles | tag index | UID once | unique `uid_lookup` | — | `FleetTests` (UID race) |
| Limit changes racing | limits | one active rule per kind and period | vehicle lock, partial unique index | — | `FleetTests.racingLimitChangesLeaveOneActiveRule` |
| Payouts against one payable | payable | ≤ available − pending | payable lock | unique bank reference | `StationFinanceTests` (sequential); see the gaps below |
| Offline batch resent | fuelings | each capture once | unique `client_txn_id` per item, per-item transaction | `DUPLICATE` result | `OfflineSyncTests` |
| Overlapping imports | plate index, jobs | no duplicate vehicle | one active job per company (partial unique), plate unique index, per-row transaction | row state, resume from lease | `FleetImportTests.overlappingImportsNeverCreateADuplicate` (two workers racing) |
| Import worker crash | job, rows | each row once | row outcome committed with the vehicle; lease and `SKIP LOCKED` | resume at the first `VALID` row | `FleetImportTests.aWorkerCrashMidwayIsResumedByAnotherWithoutDuplicates` |
| Refresh token reuse | sessions | one rotation | row lock | grace window | `SessionTests.concurrentRefreshesWithTheSameCookieDoNotSignTheUserOut` |

## Retry
- **Fueling:** retries a deadlock once (the whole attempt rolled back).
- **Other money commands:** rely on the lock order above, so no deadlock cycle exists between them. A lock timeout would surface as an error with nothing posted, and the client retries safely, because every command is idempotent: by source key, by a conditional status flip, or by `Idempotency-Key`.

## Gaps (honest list)
- **Not tested at the same instant:**
  - fueling while a vehicle is suspended;
  - fueling while a top-up is approved;
  - a payout and a reversal on the same station.

  Their safety rests on the shared row locks shown above (the vehicle `FOR SHARE` against `FOR UPDATE`; the wallet lock; the payable lock). Each is covered sequentially, not with a barrier.
- **No load test with many concurrent devices on one wallet.** See `performance-report.md`.
- **No independent Fable review** of this code yet.
