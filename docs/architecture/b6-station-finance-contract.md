# B6 contract: station finance, tickets, exports and the activity calendar

**Status:** implemented on `develop`, 2026-11-19 · **Schema:** `V9__station_finance.sql`, `V10__shifts_visible_to_operators.sql`, `V11__tickets.sql`
**Builds on:** `b3-ledger-contract.md` (ledger, wallets), `b4-fueling-contract.md`, `b5-sync-contract.md` (held payables). **ADR:** 0020 (station payables and dual control). **Roadmap delta:** A1 (station account, payouts, statement), A3 (bank-detail change control), A4 (two-person reversals), A5 (tickets), D4 (exports).

## 1. What B6 adds

| Area | Who acts | Who reads |
|---|---|---|
| Station payable and statement | the ledger (fuelings, payouts, reversals) | Ops; the station manager, for the operator's stations |
| Bank accounts under change control | Ops asks, a **different** Ops/Super Admin user approves | Ops; the station manager, masked |
| Payouts with dual control above a threshold | Ops records; a different person approves large ones | Ops; the station manager, posted only |
| Held offline payables | Ops releases, with a reason | Ops |
| Fueling reversals | Ops asks, a different person approves | Ops; the company and the station, approved only |
| Support tickets | the company and the station open them; Ops answers | each side its own; Ops all |
| CSV exports of fuelings | — | each audience its own rows |
| Activity calendar and shifts | — | each audience its own |

Station managers now **must use a second factor** (they read bank details). The sessions they opened without one end at migration.

## 2. Configuration

| Property | Default | Meaning |
|---|---|---|
| `zawed.finance.payout-approval-threshold-piastres` | 5,000,000 (50,000 EGP) | Up to it a payout posts at once; above it a second person approves |
| `zawed.finance.bank-change-hold-hours` | 72 | No payout to a station for this long after its bank account is approved |

These are policy-neutral defaults. The owner has not decided the numbers.

## 3. A station's position

```
balance   = the station's payable account (what the platform owes it; may be negative)
held      = offline fuelings held at sync, not released and not reversed
available = balance − held
a new payout may take at most  available − payouts waiting for approval
```

The three numbers are read in one statement. Every decision is taken under the station's payable lock (`Ledger.lockPayable`), after any wallet lock.

## 4. Endpoints

### Operations (`/api/v1/ops`, tag "station finance")

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /stations/{id}/payable` | OPS_READ | balance, held, available, pendingPayouts |
| `GET /stations/{id}/statement?cursor&limit` | OPS_READ | entries, newest first, with running balance and the fueling or payout behind each |
| `GET /stations/{id}/statement/summary?from&to` | OPS_READ | opening, credits, debits, closing, plus the position now |
| `GET /stations/{id}/bank-account` | OPS_READ | `{active, pending}`, masked (`accountLast4`), `no-store` |
| `POST /stations/{id}/bank-account/changes` | BANK_DETAILS_MANAGE | `{accountNumber, bankName, holderName, bankLetterReference}` |
| `GET /bank-account-changes?status&stationId&operatorId` | OPS_READ | |
| `POST /bank-account-changes/{id}/approve` | BANK_DETAILS_APPROVE | never by the requester; starts the hold; supersedes the old account |
| `POST /bank-account-changes/{id}/reject` | BANK_DETAILS_APPROVE | `{reason}` |
| `GET /payouts?stationId&operatorId&status`, `GET /payouts/{id}` | OPS_READ | |
| `POST /stations/{id}/payouts` | PAYOUTS_MANAGE | `{amountPiastres, bankReference, valueDate, note?}`; `POSTED` at once, or `PENDING` above the threshold |
| `POST /payouts/{id}/approve` | PAYOUTS_APPROVE | never by the recorder; checks everything again, then posts |
| `POST /payouts/{id}/cancel` | PAYOUTS_MANAGE | `{reason}`; pending only |
| `GET /payable-holds?stationId&operatorId&state` | OPS_READ | |
| `POST /fuelings/{id}/payable-hold/release` | PAYABLE_HOLD_RELEASE | `{reason}` |
| `POST /fuelings/{id}/reversals` | REVERSALS_REQUEST | `{reason}`; completed fuelings only |
| `GET /reversals?status&companyId&operatorId&stationId&fuelingId`, `GET /reversals/{id}` | OPS_READ | |
| `POST /reversals/{id}/approve` | REVERSALS_APPROVE | never by the requester; posts the mirror of the fueling |
| `POST /reversals/{id}/reject` | REVERSALS_APPROVE | `{reason}` |

### Station portal (STATION_READ; the operator's own stations only, otherwise 404)

`GET /station/stations/{id}/payable` (without `pendingPayouts`), `/statement`, `/statement/summary`, `/bank-account` · `GET /station/payouts?stationId` (posted only) · `GET /station/reversals?stationId` (approved only, without staff ids).

**Who acted, by name (QA round 1).** `BankAccountResponse` and `PayoutResponse` carry `requestedByName`/`requestedByRole` and `decidedByName`/`decidedByRole`. Operations reads names and roles; the station portal reads every ZAWED staff member only as role `ZAWED_OPERATIONS`, with a null name and a null `requestedBy`/`decidedBy` id (no longer required in the schema).

### Company portal (COMPANY_READ)

`GET /company/reversals`: approved reversals of the company's fuelings, without staff ids.

### Tickets

| Endpoint | Permission |
|---|---|
| `GET /company/tickets`, `GET /company/tickets/{id}` | COMPANY_READ |
| `POST /company/tickets`, `POST /company/tickets/{id}/messages` | TICKETS_OPEN (company scope only) |
| `GET /station/tickets`, `GET /station/tickets/{id}` | STATION_READ |
| `POST /station/tickets`, `POST /station/tickets/{id}/messages` | TICKETS_OPEN (operator scope only) |
| `GET /ops/tickets?companyId&operatorId&status&type`, `GET /ops/tickets/{id}` | OPS_READ |
| `POST /ops/tickets/{id}/messages` `{body, internal?}` | TICKETS_MANAGE |
| `POST /ops/tickets/{id}/status` `{status, assignToMe?}` | TICKETS_MANAGE |

- A company may open `LOST_TAG`, `STOLEN_TAG`, `LIMIT_CHANGE`, `VEHICLE_CHANGE`, `TOPUP_QUERY`, `DISPUTE`, `OTHER`.
- A station may open `DEVICE_FAULT`, `TAG_READ_ISSUE`, `PAYOUT_QUERY`, `PRICE_QUERY`, `DISPUTE`, `OTHER`.
- A ticket may link a vehicle, station or fueling. Each link must be a row its author can see, otherwise `TICKET_LINK_NOT_FOUND`. In practice:
  - A company can link its own vehicles and fuelings. It sees no stations, so it cannot link a station; it links the fueling, which names the station.
  - A station can link its own stations and the fuelings at them. It cannot link a vehicle.
  - Another tenant's row and an id that does not exist give the same answer.

  Until B7 W2 (2026-11-19), a company's station link was not checked. A company could link any operator's station, and a station id that did not exist failed with a 500.
- Ops answering an open ticket moves it to `IN_PROGRESS`. Internal notes are never shown to the company or the station. A `CLOSED` ticket takes no replies.

### Exports, calendar, shifts

| Endpoint | Who | Notes |
|---|---|---|
| `GET /{ops,company,station}/fuelings/export?from&to&lang` | each portal | CSV (UTF-8 with BOM for Excel), at most 366 days and 50,000 rows, formula injection defused |
| `GET /{ops,company,station}/activity?from&to&…` | each portal | one row per Cairo day: completed, declined, offline, with conflicts, volume, amount, alerts. Ops filters by company, station, vehicle, device or agent; a company by vehicle; a station by station, device or agent |
| `GET /ops/shifts`, `GET /station/shifts` | Ops, station manager | agent sessions (shift start, end or open), for the shift view and the "last shift" filter |

## 5. Error codes added

| Code | Status | When |
|---|---|---|
| `SELF_APPROVAL_NOT_ALLOWED` | 403 | Deciding your own bank change, payout or reversal |
| `BANK_ACCOUNT_REQUIRED` | 409 | A payout to a station without a verified account |
| `PAYOUT_ON_HOLD` | 409 | A payout during the bank-change hold |
| `PAYOUT_EXCEEDS_AVAILABLE` | 409 | More than available − pending |
| `BANK_REFERENCE_IN_USE` | 409 | The transfer reference already pays another payout |
| `VALUE_DATE_TOO_FAR` | 400 | A value date more than 7 days ahead |
| `PAYOUT_NOT_PENDING`, `PAYOUT_BANK_ACCOUNT_CHANGED` | 409 | Approving or cancelling a decided payout; the account changed while it waited |
| `BANK_ACCOUNT_CHANGE_PENDING`, `BANK_ACCOUNT_CHANGE_NOT_PENDING`, `INVALID_BANK_ACCOUNT` | 409, 409, 400 | Bank change rules |
| `FUELING_NOT_HELD`, `HOLD_ALREADY_RELEASED` | 409 | Releasing a payable that is not held, or twice |
| `FUELING_NOT_REVERSIBLE`, `REVERSAL_EXISTS`, `FUELING_REVERSED`, `REVERSAL_NOT_PENDING` | 409 | Reversal rules |
| `TICKET_TYPE_NOT_ALLOWED`, `TICKET_LINK_NOT_FOUND`, `TICKET_CLOSED` | 400, 400, 409 | Ticket rules |
| `EXPORT_RANGE_TOO_LONG` | 400 | An export longer than 366 days |
| `INVALID_PERIOD` | 400 | A statement period that ends before it starts |

## 6. Not in B6 (open)

- Whether a reversal gives back the vehicle's limit usage and clears the tag's cooldown (it does neither today).
- The threshold and hold values (owner).
- Payout requests by stations, bank statement import and automatic matching (V1.5).
- Bank account change requests by the station manager (QA round 1 found `POST /station/.../bank-account/changes` missing). Not built: this contract and ADR 0020 §8 ("Portals read, Operations writes") give the request to Ops. The roadmap delta (§G) lists bank details under "request + Ops approval" for station self-service. Owner decision pending.
- Invoicing and tax documents (roadmap A2, legal decision pending).
