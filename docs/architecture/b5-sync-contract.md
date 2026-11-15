# B5 contract: offline capture and sync

**Status:** implemented on `develop`, 2026-11-15 · **Schema:** `V8__offline_sync.sql`
**Builds on:** `b4-fueling-contract.md` (device channel, the engine, receipts). **ADR:** 0019 (offline protocol).

A POS keeps working without the network. It downloads a **snapshot** while online, decides offline captures against it and records them in a **persistent queue**, and sends them in **batches** when the network returns. The server records every batched fueling with the same engine (`origin = OFFLINE`), never reverses one against the agent or the driver (PRD §7.5), and turns any check the snapshot got wrong into an alert.

## 1. Contract version

Every POS request carries `X-Zawed-Contract: <n>`. The server answers `426 CONTRACT_UPGRADE_REQUIRED` (with `params.minimum`) when `n` is lower than the minimum it still accepts, and treats a missing header as the current version (the web simulator). The current and minimum versions are `1`; they appear in `GET /api/v1/meta` as `posContract` and `posContractMinimum`.

## 2. The snapshot

`GET /api/v1/pos/snapshot` (device token; no agent needed, so a device can refresh before a shift). `Cache-Control: no-store`.

| Part | Content |
|---|---|
| `serverTime`, `contract` | For the device clock check and the version |
| `station` | Id, names, time zone, fuels sold, open or not |
| `prices` | For each fuel the station sells: the price in force now and any change scheduled within 7 days (`effectiveFrom`) |
| `tagKey` | A key for this device only (`HMAC(server key, "pos-tag-digest:" + deviceId)`), base64 |
| `tags` | Every tag that can fuel (`ACTIVE`, and `REPLACEMENT_PENDING` so the device can tell the agent why it is refused): `digest` = the first 8 hex characters (32 bits) of `HMAC-SHA256(tagKey, normalised UID)`, status, `cooldownUntil`, vehicle id. Tags with a 4-byte UID are left out |
| `vehicles` | Vehicles with such a tag: id, company id, plate, fuel, `vip` (always false while the VIP flow is off), tank capacity, active |
| `limits` | Active limit rules of those vehicles, each with what `remaining` in the period that contains `serverTime` (station zone) and `periodStart`; never the rule or its use |
| `companies` | Id, names, active, cooldown minutes, `walletStatus` (`AVAILABLE`, `LOW`, `INSUFFICIENT`, `SUSPENDED`), and `offlineAllowancePiastres` rounded down to 100 EGP |

What the snapshot deliberately leaves out:
- **Tag UIDs.** A UID is the vehicle's key at the pump. The device matches a scanned UID by computing the truncated digest with `tagKey`. The key ships with the digests, so their holder could brute-force a full-length digest; at 32 bits a brute force yields millions of candidate UIDs per tag and recovers none (B5 review 3). 4-byte UIDs (2^32 values) would still be identifiable and are left out: such tags are decided online only. A rare collision between two tags is resolved in favour of the active one; the server decides at sync.
- **Wallet balances.** `offlineAllowancePiastres = min(balance, zawed.sync.offline-allowance-cap-piastres)` (default 500,000, i.e. 5,000 EGP), rounded down to 100 EGP. It tells the device how much it may spend offline without revealing the balance. It bounds an honest device only; the server enforces its own bound (§4).
- **PIN hashes.** A 6-digit PIN can be brute-forced against any hash a stolen device holds. Offline, only an agent who signed in online on this device keeps working, until that session expires (12 hours). A new sign-in needs the network. Offline PIN verification waits for the Kotlin POS and its secure hardware (B8).

The device applies its own queued captures on top of the snapshot (provisional decrement of the allowance and the limits, cooldown on the tag; PRD §7.4) before deciding the next one. Captures already synced stay in that decision base until a snapshot taken after their sync replaces it. Times are the server's: the device keeps the offset between its clock and `serverTime`. A snapshot more than 24 hours old, or a station that is not open, decides nothing: the device refuses offline until it reconnects. Once a new limit period starts, the device does not know the limit (it holds what remained, not the rule) and lets the capture through; the server flags any excess.

## 3. The batch

`POST /api/v1/pos/sync` (device token; no agent needed: the queue may be sent after the shift).

```json
{ "batchId": "uuid", "pending": 3,
  "items": [ { "clientTxnId": "uuid", "tagUid": "04A1…", "volumeMl": 20000, "fuelType": "GAS_92",
               "deviceAt": "2026-11-15T07:58:00Z", "agentId": "uuid", "receiptLocale": "ar" } ] }
```

- At most 100 items per batch, in capture order. `pending` is what the device still holds after this batch (shown in the sync monitor).
- **Who captured it:** `agentId` must be an agent of the device's station (else `REJECTED`, `AGENT_NOT_OF_STATION`). If no session of theirs on this device covers `deviceAt` (±5 minutes), for example because the session ended elsewhere while the device was offline, the capture is still recorded, with an `AGENT_UNVERIFIED` conflict.
- **Time:** a `deviceAt` more than 5 minutes ahead of server time is judged at the time the item arrived, with a `DEVICE_CLOCK_SKEW` conflict. Captures older than 7 days are recorded with a `STALE_CAPTURE` conflict.
- **Price:** `unitPricePiastres` (optional) is the price the device showed; a different server price is charged and flagged `PRICE_MISMATCH`.
- **Answer:** one result per item, in order:

| `result` | Meaning |
|---|---|
| `RECORDED` | Recorded now; carries the fueling (reference, amount, `conflicts`) |
| `DUPLICATE` | This `clientTxnId` was recorded before (same device, tag and volume); carries the stored fueling |
| `REJECTED` | Nothing could be recorded; `code` says why (`TAG_NOT_RECOGNISED`, `AGENT_NOT_OF_STATION`, `CLIENT_TXN_CONFLICT`, `IDEMPOTENCY_MISMATCH`, `INVALID_VOLUME`). The device keeps rejected items aside for Operations and never deletes them |
| `RETRY` | A fault on the server's side; nothing recorded, the device keeps the item and sends it again |

A batch sent twice changes nothing the second time: every item comes back `DUPLICATE`. Each item is its own transaction, so one bad item never blocks the rest. The batch itself is recorded (`sync_batches`) for the monitor.

## 4. Recording an offline fueling

The engine runs the same checks in the same order (B4 §3), **evaluated at `deviceAt`**, with the price in force at `deviceAt`. Then:

- **The fueling is always `COMPLETED`** (`origin = OFFLINE`) for a tag that resolves. Each check that fails is recorded in `conflicts`: the decline reasons of B4, plus `STALE_CAPTURE`, `AGENT_UNVERIFIED`, `DEVICE_CLOCK_SKEW`, `DECLINED_ONLINE`, `STATION_NOT_ACTIVE`, `PRICE_MISMATCH` and `OVER_OFFLINE_ALLOWANCE`. It raises one `SYNC_CONFLICT` alert (Operations and the company).
- **Declined online, then dispensed:** when the id was declined online (its answer lost) and the device let the fuel go offline, the capture is recorded as its own fueling under an id derived from the first, flagged `DECLINED_ONLINE`. A second send finds it (`DUPLICATE`).
- **No price at `deviceAt`:** the price in force now is charged, and `FUEL_NOT_AVAILABLE` is kept as a conflict.
- **Server-side bound** (B5 review 2): one device's offline fuelings for one company in 24 hours may not exceed `zawed.sync.offline-exposure-cap-piastres` (default 1,000,000, i.e. 10,000 EGP). Past it the fueling is still recorded, flagged `OVER_OFFLINE_ALLOWANCE`, and its payable **held** (`payable_held`; B6 payouts exclude it until Operations clears it), and a critical `OFFLINE_EXPOSURE` alert is raised. A station that was not open also holds the payable. The device is not suspended automatically.
- **Money**, when billable and the amount is above zero: the station payable is credited the full amount. The company wallet is debited what it holds, up to the amount. The remainder, if any, is debited to `company:{id}:receivable`: money the company owes for fuel it took while its wallet could not cover it. The wallet never goes below zero. Who settles a receivable (the company on its next top-up, or the platform) is an open owner decision; the ledger records the debt either way.
- **Cooldown:** the tag's cooldown becomes the later of its current value and `deviceAt + cooldown`.
- **Limits and the VIP ceiling** count the fueling in the period of `deviceAt`.
- **Tank capacity** raises its alert as online.

A `REJECTED` item raises a `SYNC_REJECTED` alert for Operations (device, code, `clientTxnId`, the UID's last four characters), because fuel was dispensed that the system could not record.

**Unknown online outcome** (B5 review 6): the terminal saves each online attempt before sending it. If the answer is lost and the terminal then refused the fueling (or the page reloaded), it later asks `POST /api/v1/pos/fuelings/{clientTxnId}/resolve {toldOutcome: "REFUSED"}` (device + agent session). The answer is the stored fueling, or `404 FUELING_NOT_FOUND` when the server never received it. A completed fueling the agent was told was refused raises one critical `OUTCOME_MISMATCH` alert (Operations and the company).

## 5. Sync monitor

`GET /api/v1/ops/sync/devices` (Super Admin, Ops): every active device with its station, `lastSyncAt`, the last batch (time, counts by result) and the `pending` it reported. A device not seen for more than 24 hours is flagged `stale`. `GET /api/v1/ops/sync/batches?deviceId=` lists batches, newest first. The station manager sees the same for the operator's devices (`GET /api/v1/station/sync/devices`).

## 6. Error codes added

| Code | Status | When |
|---|---|---|
| `CONTRACT_UPGRADE_REQUIRED` | 426 | The POS's contract version is below the minimum |
| `AGENT_NOT_OF_STATION` | per item | The capturing agent does not work at this device's station |
| `FUELING_NOT_FOUND` | 404 | Resolve: the server never received this fueling from this device |
| `BATCH_TOO_LARGE` | 400 | More than 100 items |
