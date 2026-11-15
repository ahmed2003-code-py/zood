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
| `tags` | Every tag that can fuel (`ACTIVE`, and `REPLACEMENT_PENDING` so the device can tell the agent why it is refused): `digest = HMAC-SHA256(tagKey, normalised UID)` in hex, status, `cooldownUntil`, vehicle id |
| `vehicles` | Vehicles with such a tag: id, company id, plate, fuel, tier, tank capacity, active |
| `limits` | Active limit rules of those vehicles, each with `used` in the period that contains `serverTime` (station zone) and `periodStart` |
| `companies` | Id, names, active, cooldown minutes, `walletStatus` (`AVAILABLE`, `LOW`, `INSUFFICIENT`, `SUSPENDED`), and `offlineAllowancePiastres` |

What the snapshot deliberately leaves out:
- **Tag UIDs.** A UID is the vehicle's key at the pump. The device matches a scanned UID by computing its digest with `tagKey`. A stolen device yields digests under its own key only, and brute-forcing a 56-bit UID is impractical.
- **Wallet balances.** `offlineAllowancePiastres = min(balance, zawed.sync.offline-allowance-cap-piastres)` (default 500,000, i.e. 5,000 EGP). It tells the device how much it may spend offline without revealing the balance, and bounds what an offline device can overspend.
- **PIN hashes.** A 6-digit PIN can be brute-forced against any hash a stolen device holds. Offline, only an agent who signed in online on this device keeps working, until that session expires (12 hours). A new sign-in needs the network. Offline PIN verification waits for the Kotlin POS and its secure hardware (B8).

The device applies its own queued captures on top of the snapshot (provisional decrement of the allowance and the limits, cooldown on the tag; PRD §7.4) before deciding the next one.

## 3. The batch

`POST /api/v1/pos/sync` (device token; no agent needed: the queue may be sent after the shift).

```json
{ "batchId": "uuid", "pending": 3,
  "items": [ { "clientTxnId": "uuid", "tagUid": "04A1…", "volumeMl": 20000, "fuelType": "GAS_92",
               "deviceAt": "2026-11-15T07:58:00Z", "agentId": "uuid", "receiptLocale": "ar" } ] }
```

- At most 100 items per batch, in capture order. `pending` is what the device still holds after this batch (shown in the sync monitor).
- **Who captured it:** `agentId` must be an agent of the device's station with a session **on this device** that was open at `deviceAt` (created before it, not ended before it). This is how an offline capture is tied to a person without a network.
- **Time:** `deviceAt` may not be more than 5 minutes ahead of server time (`DEVICE_CLOCK_SKEW` for that item). Captures older than 7 days are still recorded, with a `STALE_CAPTURE` conflict.
- **Answer:** one result per item, in order:

| `result` | Meaning |
|---|---|
| `RECORDED` | Recorded now; carries the fueling (reference, amount, `conflicts`) |
| `DUPLICATE` | This `clientTxnId` was recorded before (same device, tag and volume); carries the stored fueling |
| `REJECTED` | Nothing recorded; `code` says why (`TAG_NOT_RECOGNISED`, `AGENT_NOT_ON_DEVICE`, `DEVICE_CLOCK_SKEW`, `CLIENT_TXN_CONFLICT`, `IDEMPOTENCY_MISMATCH`, `INVALID_VOLUME`) |

A batch sent twice changes nothing the second time: every item comes back `DUPLICATE`. Each item is its own transaction, so one bad item never blocks the rest. The batch itself is recorded (`sync_batches`) for the monitor.

## 4. Recording an offline fueling

The engine runs the same checks in the same order (B4 §3), **evaluated at `deviceAt`**, with the price in force at `deviceAt`. Then:

- **The fueling is always `COMPLETED`** (`origin = OFFLINE`). Each check that fails is recorded in `conflicts` (the decline reasons of B4, plus `STALE_CAPTURE`) and raises one `SYNC_CONFLICT` alert (Operations and the company).
- **Money**, when billable and the amount is above zero: the station payable is credited the full amount. The company wallet is debited what it holds, up to the amount. The remainder, if any, is debited to `company:{id}:receivable`: money the company owes for fuel it took while its wallet could not cover it. The wallet never goes below zero. Who settles a receivable (the company on its next top-up, or the platform) is an open owner decision; the ledger records the debt either way.
- **Cooldown:** the tag's cooldown becomes the later of its current value and `deviceAt + cooldown`.
- **Limits and the VIP ceiling** count the fueling in the period of `deviceAt`.
- **Tank capacity** raises its alert as online.

A `REJECTED` item raises a `SYNC_REJECTED` alert for Operations (device, code, `clientTxnId`, the UID's last four characters), because fuel was dispensed that the system could not record.

## 5. Sync monitor

`GET /api/v1/ops/sync/devices` (Super Admin, Ops): every active device with its station, `lastSyncAt`, the last batch (time, counts by result) and the `pending` it reported. A device not seen for more than 24 hours is flagged `stale`. `GET /api/v1/ops/sync/batches?deviceId=` lists batches, newest first. The station manager sees the same for the operator's devices (`GET /api/v1/station/sync/devices`).

## 6. Error codes added

| Code | Status | When |
|---|---|---|
| `CONTRACT_UPGRADE_REQUIRED` | 426 | The POS's contract version is below the minimum |
| `AGENT_NOT_ON_DEVICE` | per item | The capturing agent had no session on this device at `deviceAt` |
| `BATCH_TOO_LARGE` | 400 | More than 100 items |
