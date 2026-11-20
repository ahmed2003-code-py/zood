# API changes: production hardening

Base path `/api/v1`. Every endpoint is in Swagger (`/api/v1/docs`) and the committed `apps/api/openapi/openapi.json`.

## Breaking changes (Ops console only; the web client is updated in the same branch)

| Before | After | Why |
|---|---|---|
| `POST /ops/topups/{id}/confirm` (credited the company's figure) | **Removed.** Use `POST /ops/topups/{id}/verify`, then `POST /ops/topups/{id}/approve` | Maker-checker; credit the verified amount (ADR 0021) |
| `POST /ops/companies/{id}/wallet/credits` credited at once, 201 `CONFIRMED` | Records a verified transfer: 201 `PENDING_APPROVAL`; a repeat of the same record (same company, amount, transfer date, evidence and note) answers 200 with the same record; the same reference with anything else is 409 `BANK_REFERENCE_IN_USE` | No single-person money |
| `TopUpResponse` | Adds `origin`, `receivingAccount`, `verifiedAmountPiastres`, `verifiedBy`, `verifiedAt`, `evidenceReference`; `status` adds `PENDING_APPROVAL` | |
| `POST /ops/companies` always 201 | 201 when created; **200** when an `Idempotency-Key` repeat answers the earlier company | ADR 0022 |
| `VehicleBody.tankCapacityMl` ≤ 2,000,000 | ≤ **1,500,000**; adds optional `fleetCode`; `VehicleResponse` adds `fleetCode` | ADR 0023 |
| Roles | Adds `OPS_MANAGER` (platform, MFA required) | |

## New endpoints

| Endpoint | Permission | Body / query | Answer |
|---|---|---|---|
| `GET /ops/topups/receiving-accounts` | OPS_READ | — | `["MAIN", …]` (`zawed.finance.receiving-accounts`) |
| `POST /ops/topups/{id}/verify` | WALLET_CREDIT | `{receivedAmountPiastres*, receivingAccount?, bankReference?, transferDate?, evidenceReference?, note?}` | `TopUpResponse` (`PENDING_APPROVAL`) |
| `POST /ops/topups/{id}/approve` | **TOPUP_APPROVE** | — | `TopUpResponse` (`CONFIRMED`); repeat returns it unchanged |
| `GET /ops/wallet-adjustments` | OPS_READ | `status`, `companyId`, `cursor`, `limit` | Page of `WalletAdjustmentResponse` |
| `POST /ops/companies/{id}/wallet/adjustments` | WALLET_CREDIT | `{amountPiastres*, reason*}`, header `Idempotency-Key*` (required since B7 W1) | 201 `WalletAdjustmentResponse` (`PENDING_APPROVAL`); a repeat with the same key answers the same adjustment |
| `POST /ops/wallet-adjustments/{id}/approve` | TOPUP_APPROVE | — | `APPROVED` (posts once) |
| `POST /ops/wallet-adjustments/{id}/reject` | TOPUP_APPROVE | `{reason*}` | `REJECTED` |
| `POST /ops/wallet-adjustments/{id}/cancel` | WALLET_CREDIT (requester only) | — | `CANCELLED` |
| `GET /ops/companies/{id}/onboarding` | OPS_READ | — | `{companyId, ready, checks[{key, done, required, count}]}`. Keys: ACTIVE, CONTACT, REGISTRATION, DEPARTMENTS, VEHICLES, TAGS, LIMITS, WALLET_FUNDED, COMPANY_USERS |
| `GET /ops/fleet-imports/template` | OPS_READ | `format=xlsx|csv`, `lang=ar|en` | The file |
| `POST /ops/companies/{id}/fleet-imports` | FLEET_MANAGE | multipart, part `file` (≤ 5 MB) | 201 `FleetImportResponse` (`VALIDATED`, counts) |
| `GET /ops/fleet-imports` | OPS_READ | `companyId`, `cursor`, `limit` | Page of `FleetImportResponse` |
| `GET /ops/fleet-imports/{id}` | OPS_READ | — | `FleetImportResponse` (poll while `QUEUED`/`RUNNING`) |
| `GET /ops/fleet-imports/{id}/rows` | OPS_READ | `status`, `problemsOnly`, `after`, `limit` (≤ 500) | `{items[{rowNumber, plate, fleetCode, status, errors[{column, code}], vehicleId}], nextAfter}` |
| `GET /ops/fleet-imports/{id}/errors.csv` | OPS_READ | — | CSV of problem rows (formula-defused) |
| `POST /ops/fleet-imports/{id}/confirm` | FLEET_MANAGE | `{mode: STRICT | VALID_ROWS_ONLY}` | `QUEUED`; a repeat answers the job as it is |
| `POST /ops/fleet-imports/{id}/cancel` | FLEET_MANAGE | — | `CANCELLED` (only before confirmation) |

## New error codes

| Code | Status | When |
|---|---|---|
| `TOPUP_NOT_AWAITING_APPROVAL` | 409 | Approving a top-up that is not `PENDING_APPROVAL` |
| `SELF_APPROVAL_NOT_ALLOWED` | 403 | Approving your own verification or adjustment request |
| `UNKNOWN_RECEIVING_ACCOUNT` | 400 | Not a configured ZAWED account (`params.accounts`) |
| `ADJUSTMENT_NOT_PENDING` | 409 | Adjustment already decided |
| `IDEMPOTENCY_KEY_REUSED` | 409 | Same key, different body |
| `INVALID_IDEMPOTENCY_KEY` | 400 | Key not 8–200 characters; or missing or blank where the key is required (wallet adjustments) |
| `FLEET_CODE_IN_USE` | 409 | Fleet code taken in the company |
| `INVALID_TANK_CAPACITY`, `INVALID_YEAR` | 400 | Vehicle rules |
| `IMPORT_FILE_REQUIRED`, `IMPORT_FILE_TYPE`, `IMPORT_FILE_EMPTY`, `IMPORT_FILE_ENCODING`, `IMPORT_FILE_MALFORMED`, `IMPORT_FILE_UNSAFE`, `IMPORT_HEADER_INVALID`, `IMPORT_TOO_MANY_ROWS` | 400 | The upload |
| `IMPORT_FILE_TOO_LARGE` | 413 | Over 5 MB |
| `IMPORT_HAS_PROBLEMS`, `IMPORT_NOTHING_TO_IMPORT`, `IMPORT_ALREADY_RUNNING`, `IMPORT_NOT_CONFIRMABLE`, `IMPORT_NOT_CANCELLABLE` | 409 | Confirm or cancel |

Row error codes (in `errors[].code`): `REQUIRED`, `TOO_LONG`, `INVALID_PLATE`, `INVALID_YEAR`, `INVALID_FUEL_TYPE`, `INVALID_TANK_CAPACITY`, `UNKNOWN_DEPARTMENT`, `INVALID_TIER`, `INVALID_NUMBER`, `VIP_CEILING_ONLY_FOR_VIP`, `INVALID_LIMIT`, `DUPLICATE_PLATE_IN_FILE`, `DUPLICATE_FLEET_CODE_IN_FILE`, `PLATE_IN_USE`, `FLEET_CODE_IN_USE`.

## Unchanged
The POS contract (`X-Zawed-Contract: 1`), fueling, sync, station finance and tickets endpoints. One addition: the alert type `COMPANY_OFFLINE_EXPOSURE` may appear in `/ops/alerts`.
