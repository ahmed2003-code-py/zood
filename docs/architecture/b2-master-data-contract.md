# B2 contract: master data and Ops onboarding

**Status:** implemented on `develop`, 2026-11-13 · **Schema:** `V4__master_data.sql`, `V5__rls_binds_runtime_role.sql`
**Builds on:** `b1-identity-contract.md` (sign-in, permissions, error envelope, pagination, audit).

## 1. Who can do what

| Permission | Roles | Covers |
|---|---|---|
| `OPS_READ` | Super Admin, Ops | Every `GET /api/v1/ops/...` |
| `COMPANIES_MANAGE` | Super Admin, Ops | Onboard, edit, freeze companies; add departments |
| `COMPANY_POLICY_MANAGE` | Super Admin | Cooldown window and low-balance threshold |
| `FLEET_MANAGE` | Super Admin, Ops | Vehicles, RFID tags, limits |
| `NETWORK_MANAGE` | Super Admin, Ops | Operators, stations, POS devices, agents |
| `PRICES_MANAGE` | Super Admin, Ops | Schedule fuel prices |
| `COMPANY_READ` | Fleet Manager, Accountant | `GET /api/v1/company/...` |
| `STATION_READ` | Station Manager | `GET /api/v1/station/...` |

The company and station portals are **read-only** (approved decision: changes go through requests to Operations). Their scope always comes from the signed-in user, never from the request, and row-level security hides every other tenant's rows even when an id is guessed: a row outside the caller's scope answers `404`, exactly like one that does not exist.

## 2. Endpoints

All lists are paginated (`?cursor=&limit=`, newest first) and return `{items, nextCursor}`. The one exception is inside the vehicle detail: a vehicle's tags (its whole tag history, at most 100) and its active limits (at most six: two kinds × three periods) come as plain arrays.

**Companies** (`/api/v1/ops/companies`): `GET` (filter `status`, `q` on names and tax id), `POST`, `GET /{id}`, `PUT /{id}`, `PUT /{id}/policy` (reason required), `POST /{id}/status` (`ACTIVE` | `FROZEN`, reason required), `GET|POST /{id}/departments`.
**Company portal:** `GET /api/v1/company/profile`, `GET /api/v1/company/departments`, `GET /api/v1/company/vehicles`, `GET /api/v1/company/vehicles/{id}`.

**Fleet** (`/api/v1/ops`): `GET|POST /vehicles`, `GET|PUT /vehicles/{id}`, `POST /vehicles/{id}/status`, `POST /vehicles/{id}/tags`, `POST /tags/{id}/status`, `PUT /vehicles/{id}/limits`, `DELETE /vehicles/{id}/limits/{limitId}`. The vehicle detail returns the vehicle, its tags and its active limits in one call.

**Network** (`/api/v1/ops`): `GET|POST /operators`, `GET|PUT /operators/{id}`, `POST /operators/{id}/status`; the same for `/stations`; `GET|POST /devices`, `GET /devices/{id}`, `POST /devices/{id}/activation-code`, `POST /devices/{id}/revoke`; `GET|POST /agents`, `GET /agents/{id}`, `POST /agents/{id}/reset-pin`, `POST /agents/{id}/status`.
**Station portal:** `GET /api/v1/station/operator`, `/stations`, `/stations/{id}`, `/devices`, `/agents`.

**Prices** (`/api/v1/ops/prices`): `GET /current?stationId=`, `GET` (history and scheduled, filter `fuelType`, `stationId`, `nationalOnly`), `POST`.

## 3. Rules

**Companies.** Cooldown 120–240 minutes, default 180 (PRD 6.4). Tax id unique when given. Department names unique per company, compared case-insensitively with spaces collapsed. A department budget is monitoring only and never blocks a fueling.

**Vehicles.**
- The plate is stored and shown exactly as registered. A comparison key finds duplicates whatever the spelling: spaces and dashes, Eastern Arabic digits and Arabic letter variants fold together. A plate belongs to one live vehicle; decommissioning frees it.
- A vehicle never changes company. Its department must be an active department of the same company; the database refuses any cross-tenant child (`check_same_tenant`).
- A VIP monthly ceiling applies to VIP vehicles only and is **never shown to the company** (PRD 4.5: an invisible safeguard that only flags to Ops).
- Decommissioning is final and, in the same transaction, deactivates the vehicle's tags and retires its limits.

**RFID tags.**
- The UID (8–32 hex characters; readers' spaces, colons and dashes are accepted) is never stored in clear: AES-256-GCM bound to the tag id, plus an HMAC that finds it. Responses only ever contain the last four characters.
- A physical tag is bound **once, ever**: a lost or stolen tag cannot be bound again, to any vehicle.
- At most one active tag per vehicle. A working tag is never retired silently: it must be reported first.
- Transitions: `ACTIVE` → `LOST` | `STOLEN` | `DEACTIVATED` | `REPLACEMENT_PENDING`; `REPLACEMENT_PENDING` → `ACTIVE` | `LOST` | `STOLEN` | `DEACTIVATED`. Lost, stolen and deactivated are final.
- Issuing a new tag closes a pending replacement and **carries the cooldown over**, so swapping tags cannot shorten the cooling-off period.

**Limits.** One active rule per vehicle, kind (`VOLUME` in millilitres, `AMOUNT` in piastres) and period (`DAY`, `WEEK`, `MONTH`). Setting a rule retires the previous one instead of editing it, so what applied when stays readable.

**Stations.** Time zone is an IANA id, default `Africa/Cairo`; daily rules will be evaluated in it. Latitude and longitude come together or not at all. A closed station is final and gets no new device or agent.

**POS devices.** Registered `PENDING_ACTIVATION` with an activation code of 12 characters from a 31-letter alphabet (about 59 bits), valid 72 hours, stored only as a keyed hash, **shown once**, sent with `Cache-Control: no-store`. A new code can be issued only while pending and replaces the old one. Revocation is final and clears the code. Activation itself is B5.

**Agents.** Worker ID `W` + 7 digits, generated. The 6-digit PIN is generated by the server (never chosen by Ops), never a trivial pattern (one digit repeated, a straight run), hashed with Argon2id, **shown once** with `no-store`, and must be changed at first sign-in. A PIN reset clears any lockout. Deactivation is final.

**Prices.** Insert-only: a change is a new row with `effectiveFrom`. Never in the past: a time up to one minute early is accepted for clock differences but stored as taking effect now, so a fueling already priced keeps its price. At most 366 days ahead, one price per fuel, place and instant. The price at a station at a moment is whichever of the national price and the station's override **took effect last**, so a national change supersedes an older override.

**Audit.** Every change is audited in the same transaction, under the scope of the tenant it concerns, with a reason where one is required. Secrets (UIDs, codes, PINs) never appear in audit metadata, logs or later responses.

## 4. Error codes added

| Code | Status | When |
|---|---|---|
| `TAX_ID_IN_USE` | 409 | Company or operator tax id taken |
| `DEPARTMENT_EXISTS` | 409 | Same name in the company |
| `COMPANY_NOT_FOUND`, `OPERATOR_NOT_FOUND`, `STATION_NOT_FOUND`, `DEPARTMENT_NOT_FOUND` | 400 | A referenced parent in a request body does not exist (or is outside scope) |
| `SCOPE_NOT_FOUND` | 400 | Creating a user for a company or operator that does not exist |
| `PLATE_IN_USE`, `INVALID_PLATE` | 409, 400 | Plate rules |
| `VIP_CEILING_ONLY_FOR_VIP` | 400 | Ceiling on a normal vehicle |
| `VEHICLE_DECOMMISSIONED`, `VEHICLE_NOT_ACTIVE` | 409 | Vehicle state |
| `INVALID_TAG_UID`, `TAG_ALREADY_BOUND`, `VEHICLE_HAS_ACTIVE_TAG`, `INVALID_TAG_TRANSITION` | 400, 409 | Tag rules |
| `INVALID_TIME_ZONE`, `INCOMPLETE_LOCATION`, `STATION_CLOSED` | 400, 400, 409 | Station rules |
| `DEVICE_NOT_PENDING`, `AGENT_DEACTIVATED` | 409 | Device and agent state |
| `PRICE_BACKDATED`, `PRICE_TOO_FAR_AHEAD`, `PRICE_EXISTS` | 400, 400, 409 | Price rules |
| `CONCURRENT_CHANGE` | 409 | A generated code collided with a concurrent request; retry |

## 5. Not in B2

- Company or station requests to Ops (lost tag reported from the portal, limit change requests): a later phase. Until then Ops acts on a phone call.
- Cancelling a scheduled price before it takes effect.
- Prices visible to the company and station portals.
- Device activation and agent PIN sign-in (B5), wallet and ledger (B3), the fueling engine (B4).
