# B4 contract: the device channel and online fueling

**Status:** implemented on `develop`, 2026-11-13 · **Schema:** `V7__devices_agents_fuelings.sql`
**Builds on:** `b2-master-data-contract.md` (devices, agents, tags, limits, prices), `b3-ledger-contract.md` (wallets, postings). **ADRs:** 0010 (device channel), 0018 (fueling engine).

## 1. The device channel

A POS terminal never holds a user token, and a user token never works on a POS route. Requests to `/api/v1/pos/**` go through their own security chain, checked before the users' one.

| Step | Call | Proof | Answer |
|---|---|---|---|
| Activate (once) | `POST /pos/activate {deviceCode, activationCode, publicKey}` | The one-time code Ops read to the installer (15 minutes, 5 tries) | The device becomes `ACTIVE` and its EC P-256 public key (X.509 SPKI, base64) is bound to it |
| Device token | `POST /pos/token {deviceId, timestamp, nonce, signature}` | ECDSA P-256/SHA-256 signature over `zawed-device-token\n{deviceId}\n{timestamp}\n{nonce}`; timestamp within ±5 minutes; nonce (16–128 chars) never used before by this device | A 15-minute HS256 token, `typ=device`, signed with a key derived from the JWT secret under its own label |
| Agent sign-in | `POST /pos/agents/login {workerId, pin}` with the device token | Worker ID + 6-digit PIN (Argon2id); the agent must belong to the device's station | A 12-hour session token, sent as `X-Agent-Session` |
| Change PIN | `POST /pos/agents/pin {currentPin, newPin}` | The current PIN | 204 |
| Sign out | `POST /pos/agents/logout` | The session | 204 |

- Every answer that carries a credential is `Cache-Control: no-store`. Session tokens and activation codes are stored only as hashes; the private key never leaves the device.
- **On every request** the device is re-read (`ACTIVE` and its operator not suspended, else 401) and so is the agent session (not revoked or expired, agent `ACTIVE` and still at the device's station). Revoking a device, suspending its operator or suspending an agent takes effect on the next request.
- **Sessions end** when the agent is suspended or deactivated, when Ops resets the PIN, and when the PIN lockout trips. Reactivating an agent does not revive an old session.
- **One live session per agent.** Signing in on another device ends the first session.
- **A new agent must change the PIN** (Ops generates it). Until then only `/pos/agents/pin` and `/pos/agents/logout` work; anything else answers `403 PIN_CHANGE_REQUIRED`.
- **Lockout:** 5 wrong PINs lock the agent for 15 minutes (`423 AGENT_LOCKED`, `params.retryAfterSeconds`). A wrong current PIN on a PIN change counts towards the same lockout. An unknown worker, a worker of another station and a wrong PIN all answer `401 AGENT_SIGN_IN_FAILED` in the same time. (Accepted: after five tries a real worker answers `423` and an unknown one keeps answering `401`, and the wrong-PIN path writes a row the unknown path does not. A worker ID is printed on a staff card, so it is not treated as secret.)
- **Activation reveals nothing:** an unknown device, a wrong, expired, used or exhausted code and a revoked device all answer `401 ACTIVATION_FAILED`.
- **Rate limits:** activation 10/min per address; token requests 60/min per address before the signature is checked and 20/min per device after it (a device id is not a secret, so it cannot key a limit on its own); agent sign-in 10/min per device; PIN change 10/min per agent; any POS call 120/min per device; receipt links 30/min per address.
- **Nonces** are kept indefinitely for now. A future purge must keep at least twice the clock window (10 minutes), because a request signed 5 minutes ahead stays acceptable for 10.

**Scope.** A device has no user and so no tenant scope. The device channel, the agent sessions, the fueling engine and the receipt read run in the platform scope, entered explicitly (`DbScope.runAs`) only after their proof is checked. `ArchitectureTests` keeps the list of such places closed.

## 2. Recording a fueling

`POST /api/v1/pos/fuelings` (device token + agent session):

```json
{ "clientTxnId": "uuid generated once per fueling", "tagUid": "04A1B2C3D4E5F6",
  "volumeMl": 20000, "fuelType": "GAS_92", "deviceAt": "2026-11-13T10:00:00Z", "receiptLocale": "ar" }
```

`fuelType` is optional (the vehicle's own fuel). The answer is `201` for a new fueling and `200` with `replayed: true` for a `clientTxnId` this device sent before for the same tag and volume. The same id from another device is `409 CLIENT_TXN_CONFLICT`; for another tag or volume, `409 IDEMPOTENCY_MISMATCH`.

The answer carries the status (`COMPLETED` or `DECLINED` with `declineReason`), the reference (`F-10001`), the plate, company and station names, the agent's worker ID, fuel, volume, unit price, amount, `billable`, `cooldownUntil`, `receiptPath`, and a **wallet status word** (`AVAILABLE`, `LOW`, `INSUFFICIENT`, `SUSPENDED`), never the balance.

A declined fueling is a normal answer (201 with `status: DECLINED`), recorded like a completed one. Errors that record nothing: `404 TAG_NOT_RECOGNISED` (a UID never bound), `409 STATION_NOT_ACTIVE`, `400 INVALID_VOLUME` (1 ml to 1,500 L).

## 3. The decision

In one transaction:

1. A `clientTxnId` seen before returns the stored outcome.
2. Lock the company wallet, then the vehicle (shared), then the tag: always this order. Fleet changes lock the vehicle before its tags, so a tag reported stolen while it is in use waits instead of deadlocking. Every fueling of one company is serialised by the wallet lock: a limit or the balance cannot be raced. A deadlock victim is retried once (the attempt rolled back entirely).
3. Read the facts under the locks and decide (`rules.FuelingRules`), **in PRD order**. The first failing check is the reason:

| # | Check | Reason |
|---|---|---|
| 1 | The tag's cooldown has passed | `COOLDOWN` |
| 2 | The tag is `ACTIVE` | `TAG_INACTIVE` |
| 3 | The vehicle is `ACTIVE` | `VEHICLE_INACTIVE` |
| 4 | The company is not frozen | `COMPANY_FROZEN` |
| 5 | The selected fuel is the vehicle's | `FUEL_TYPE_MISMATCH` |
| 6 | The station sells it and has a price in force now | `FUEL_NOT_AVAILABLE` |
| 7 | Every active limit (volume or amount, per day, week or month in the station's time zone; weeks start on Saturday) holds this fueling | `LIMIT_EXCEEDED` |
| 8 | The wallet covers the amount | `INSUFFICIENT_BALANCE` |

4. The balance is checked **before** anything is written, so the ledger posting never refuses.
5. A completed, billable fueling with an amount posts wallet → `station:{id}:payable`. Then the fueling row is written (never updated), the tag's cooldown starts (the company's cooldown, 120–240 minutes), and hard alerts are raised.

**VIP vehicles: switched off for the pilot.** The plan (§1) models the VIP flow but keeps it off until the owner decides who pays for VIP fuel. With `zawed.fueling.vip-enabled=false` (the default) a VIP vehicle fuels exactly like a normal one: limits, balance, billed. With it on, VIP skips the limits and the wallet and is not billed (`billable: false`, nothing posted; the station is then owed fuel the ledger does not record, which is why it stays off), and its monthly ceiling flags without blocking.

**Amount:** volume × price per litre, rounded half up to the piastre (`MoneyMath.amountForVolume`). The price is the one in force at the station at server time (B2 §3 Prices).

## 4. Hard alerts

Raised in the fueling's transaction, never blocking it:

| Type | When | Severity | Company sees it |
|---|---|---|---|
| `TANK_CAPACITY_EXCEEDED` | A completed fueling is larger than the vehicle's tank | Critical | Yes |
| `VIP_CEILING_PASSED` | A VIP vehicle's month crosses its ceiling (raised once, on the crossing; only while the VIP flow is on) | Warning | **No** (PRD §4.5) |
| `LOW_BALANCE` | A posting takes the wallet from at-or-above its threshold to below it (B3 `WalletLowBalance`) | Warning | Yes |

Ops lists alerts (`GET /ops/alerts?companyId=&status=&type=`) and acknowledges them (`POST /ops/alerts/{id}/acknowledge`, permission `ALERTS_MANAGE`, audited). The company reads its own (`GET /company/alerts`).

## 5. Reading fuelings

| Endpoint | Who | Filters |
|---|---|---|
| `GET /ops/fuelings`, `/ops/fuelings/{id}` | Super Admin, Ops | `companyId`, `operatorId`, `stationId`, `vehicleId`, `status`, `from`, `to` |
| `GET /company/fuelings`, `/company/fuelings/{id}` | Fleet Manager, Accountant | `vehicleId`, `status`, `from`, `to` |
| `GET /station/fuelings`, `/station/fuelings/{id}` | Station Manager | `stationId`, `status`, `from`, `to` |

Paginated, newest first. Row-level security shows a company its own fuelings and an operator the fuelings at its stations; a row outside the caller's scope answers 404. A fueling stores the plate, company, station and agent as they were at the pump, so each audience reads it without seeing the other's master data and a later rename never rewrites a receipt. No response contains a tag id or UID. A fueling row is immutable for every role, the owner included (V7_1), and the database checks that its company, operator, station, device and agent agree with each other.

## 6. Receipts

`receiptPath` is `/api/v1/receipts/{fuelingId}?sig={signature}`: a keyed signature of the fueling id, unguessable and naming exactly one receipt. `GET` on it needs no sign-in and returns the printable receipt (reference, plate, company, station, the agent's worker ID with all but its last three characters masked, fuel, volume, price, amount, time, locale) with no ids. A missing or wrong signature answers 404. The link does not expire: it is the driver's proof of purchase.

## 7. Error codes added

| Code | Status | When |
|---|---|---|
| `ACTIVATION_FAILED` | 401 | Any failed activation |
| `INVALID_DEVICE_KEY` | 400 | Not an EC P-256 SPKI key |
| `DEVICE_UNAUTHENTICATED` | 401 | No, invalid or expired device token; bad signature; device not `ACTIVE` |
| `DEVICE_CLOCK_SKEW` | 401 | Token request timestamp more than 5 minutes off; `params.serverTime` |
| `DEVICE_REPLAY` | 401 | A nonce used before |
| `AGENT_SIGN_IN_FAILED` | 401 | Wrong worker or PIN, or a worker of another station |
| `AGENT_LOCKED` | 423 | Five wrong PINs; `params.retryAfterSeconds` |
| `AGENT_DISABLED` | 403 | The agent is suspended or deactivated |
| `AGENT_SESSION_REQUIRED`, `AGENT_SESSION_INVALID` | 401 | No agent signed in; the session ended |
| `PIN_CHANGE_REQUIRED`, `PIN_TOO_WEAK`, `PIN_REUSED` | 403, 400, 400 | PIN rules |
| `TAG_NOT_RECOGNISED` | 404 | A UID never bound to a vehicle |
| `STATION_NOT_ACTIVE` | 409 | The device's station is suspended or closed |
| `INVALID_VOLUME` | 400 | Outside 1 ml to 1,500 L |
| `CLIENT_TXN_CONFLICT`, `IDEMPOTENCY_MISMATCH` | 409 | `clientTxnId` reused by another device or for another fueling |

## 8. Not in B4

- Offline fuelings and their sync (B5). The `origin` column already says `ONLINE` or `OFFLINE`.
- The POS web simulator on the live API, and the fuelings and alerts screens (B4 web, next).
- Platform fee legs on a fueling (open commercial question).
- Live updates by server-sent events (plan §3.7).
