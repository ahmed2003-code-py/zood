# Investor demo — runbook

## Start

```bash
cd apps/web
npm install          # first time only
npm run build && npm run preview   # production build at http://localhost:4173
# or, for development: npm run dev  → http://localhost:5173
```

Open **/demo**. "Start investor demo" resets the demo and opens the Company portal.

For the two-window setup, use "Open company view" and "Open POS view" on /demo (desktop), or open
these side by side:

- `/company` — the company portal (left, wide)
- `/pos` — the station terminal (right, narrow)

Both windows share the demo state and update live.

## Presenter panel

Press **Ctrl + Shift + P** (or add `?presenter=1` to any URL). Hidden by default.

- **Perspective** — Company · Station · Operations · POS
- **Network** — Online / Offline · **Printer** — Available / Failed
- **RFID tag for the next scan** — Main demo (أ ب ج 4582), Offline demo (ل م ن 5521), Daily limit (س م ر 1274), VIP, Low balance, Suspended account, Tank anomaly
- **Prepare main demo** — resets everything and opens the Company portal
- **Reset demo** — restores the exact initial state
- **Trigger tank-capacity alert** — creates the 62 L / 50 L anomaly and opens the investigation
- POS PIN: **123456**

## Sequence (~10 minutes)

1. **Company** (`/company`): wallet 842,350 EGP, month spend, needs attention, spending chart. Open vehicle أ ب ج 4582 → limit 50 L/day, tag active, last fueling.
2. **POS** (`/pos`): sign in with 123456 → Scan vehicle → vehicle found (wallet shown as a word) → 40 L (= 760.00 EGP) → Review → Confirm → success → Print receipt.
3. **Live update**: the Company window shows the new transaction highlighted and the balance 841,590 — no refresh.
4. **Control**: Finish → Scan the same tag → "Fueling is not available right now — available in 3:00 h". Presenter: tag → Daily limit (س م ر 1274) → 40 L → declined, daily limit reached.
5. **Offline**: Presenter → Offline and tag → Offline demo (ل م ن 5521). POS shows the offline banner. Fuel 30 L → "Completed offline · Pending sync"; the company does not see it. Presenter → Online → "Syncing…" → "Sync complete"; the transaction appears in the company portal with its offline origin and sync time in details.
6. **Printer failure** (combine with step 5 — set Printer → Failed before the offline sale): Print → "Printing unavailable, digital receipt saved" → View receipt. The transaction is unaffected and the wallet is debited exactly once.
7. **Station** (`/station`): today's value, liters, transactions, current shift, devices (POS-NS-04 offline).
8. **Operations** (`/ops`): platform activity, POS health, pending sync. Presenter → Trigger tank-capacity alert → investigation timeline → Suspend tag.

## Reset

Presenter → **Reset demo** (or **Prepare main demo**). The reset is deterministic: أ ب ج 4582 can fuel 40 L,
its cooldown is clear, the wallet is funded, the printer is available and the network is online.
