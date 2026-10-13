# Overnight decisions (investor prototype)

Decisions made autonomously during the overnight session, with the reasoning. Each was chosen as the
simplest option consistent with the PRD v6, the approved architecture and existing project patterns.
Nothing here introduces a new financial or business rule; open business questions are listed at the end.

## Demo data

| # | Decision | Why |
|---|---|---|
| 1 | The demo station is **محطة زُود - مدينة نصر / ZOOD Station - Nasr City**, with agent محمود عادل (worker 10427) and devices POS-NS-01…04 (POS-NS-04 offline, POS-NS-03 paper low). New Cairo remains a second station. | Requested in the overnight brief. Stations are still owned by a station *operator* (`op-elite`), so ZOOD ownership of stations is not assumed. |
| 2 | Demo PIN `123456` for every seeded agent; no lockout after 5 attempts. | PRD §6.1 lockout needs Operations unlock, which is out of demo scope. Shown in the presenter panel. |
| 3 | Hero presets are exact and deterministic: أ ب ج 4582 last fueled 2 days ago (38 L), cooldown clear, 50 L daily limit free; س م ر 1274 fueled exactly 180 L today against a 200 L daily limit, so a 40 L attempt is declined; wallet 842,350 EGP. | Reset must guarantee the live scenarios. |
| 4 | Before ~03:05 Cairo time, "earlier today" outside the 3-hour cooldown does not exist; the truck's 180 L is then placed at 00:05 and its tag cooldown is cleared. | Keeps the daily-limit scenario working at any hour. |
| 5 | Vehicle ن ص ر 6215 (Corolla 2022, 50 L tank, no daily volume rule) exists for the tank-capacity anomaly. | A 62 L fueling would otherwise be declined by the 50 L daily limit before the anomaly could be shown. |
| 6 | Wallet-status scenarios use seeded companies: الدلتا للتوزيع (balance below its threshold → **Low**) and سيناء للمقاولات (frozen → **Suspended**). There is no seeded "Insufficient" vehicle. | Insufficient balance is enforced by the engine (online decline / offline cached floor) and covered by unit tests. |

## POS and transactions

| # | Decision | Why |
|---|---|---|
| 7 | RFID is simulated: "Scan vehicle" reads the tag selected in the presenter panel (default أ ب ج 4582) after a ~1 s read animation. | The brief: never pretend browser NFC is production hardware. Production uses a `TagReader` interface over the terminal SDK. |
| 8 | Order of checks follows PRD §6.1: tag cooldown first (from the tag's own record, then the central mirror), then tag status, vehicle, company, fuel, limits, wallet. Amount-dependent checks (limit/wallet for the entered quantity) run again at confirmation. | PRD. |
| 9 | Simple flow (no authorization holds): scan → verify → fuel (liters or amount) → confirm → success. VIP vehicles skip the confirmation sheet. | Approved decision + PRD §4.4. |
| 10 | Each confirmation creates its idempotency key (`clientTxnId`) once; a retry or double tap cannot charge twice. | PRD §9. Covered by unit and E2E tests. |
| 11 | The tag cooldown is written to a shared `tagMemory` (the "physical tag") on every successful fueling, online or offline. Reset clears it. | PRD §7.3; lets any POS window see the cooldown. |
| 12 | Printing is a step after success; printer failure never touches the transaction. Retry re-attempts printing only. | PRD §6.1 receipt fail-safe. |
| 13 | Receipt: 58 mm preview, ~6–8 cm. Contains brand, station, date/time, short reference, plate, fuel, liters × price, total, result, small QR. The language is the transaction's `receiptLocale`; reprints use it. | Brief. Price per liter kept on one line (useful, no extra length). |
| 14 | The QR opens `/receipt/:clientTxnId` (digital receipt). | Demo only. Production: an opaque, signed, expiring link. |

## Offline

| # | Decision | Why |
|---|---|---|
| 15 | Offline captures are queued on the device (`posQueue`), validated against the cached snapshot **with this device's pending captures applied** (provisional decrement of wallet and limits). They are not in the company records until synced. | PRD §7.1, §7.4. |
| 16 | Offline receipts carry a device-assigned reference `TX-NS01-XXXX`; the server keeps it after sync. | Production would use device id + monotonic sequence. |
| 17 | Sync starts automatically when the presenter sets the network back to Online (≈1.6 s "syncing"), in whichever window made the change; the POS resumes an interrupted sync when it opens. | PRD §7.5. |
| 18 | At sync, conflicts are committed (never reversed) and raise a `syncConflict` alert. Auto-freezing further offline spend is **not** implemented. | PRD §4.7/§7.5; freeze deferred. |

## Operations

| # | Decision | Why |
|---|---|---|
| 19 | "Trigger tank-capacity alert" creates a completed 62 L sale for ن ص ر 6215 (50 L tank), which raises a critical, non-blocking alert, then opens its investigation. | Brief; PRD §10 (flag, never block). |
| 20 | The investigation timeline is reconstructed from the transaction record (scan/resolve/authorize offsets are illustrative). "Suspend tag" sets the tag to Deactivated immediately; the next scan is declined. No two-person approval. | Demo action only, as instructed. |

## UI

| # | Decision | Why |
|---|---|---|
| 21 | Arrived data (POS → portal) is highlighted once with a short background fade; KPI values that change while open flash the same way. | "Subtle, not distracting." |
| 22 | Company Transactions default to the last 30 days, 25 rows at a time with "Show more". | Keeps the list fast and calm. |
| 23 | Charts are lazy-loaded (Recharts) so portal screens render first. | Bundle size. |

## Open business questions (unchanged, not decided tonight)

- Who pays for VIP fuel (no wallet debit, but the station is settled)?
- Who bears losses when an offline capture pushes a wallet negative?
- Commercial model (fees/commission) and legal entity for wallet funds.
- Whether stations can see company names on their transactions (shown in the demo; easy to hide).
