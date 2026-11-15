# ADR 0019: The offline protocol

**Status:** Accepted, 2026-11-15
**Source:** `docs/architecture/implementation-plan.md` §3.4 and §7 B5; PRD §7.1–§7.5; `docs/architecture/b5-sync-contract.md`

## Context
A station may lose the network for hours. The pump must keep working, and nothing captured may be lost or charged twice. B5 has to fix:
- what a terminal is allowed to know;
- how it decides on its own;
- how its captures are recorded;
- what happens to money its snapshot thought was there and was not.

The owner has not yet decided who bears an offline overspend (plan, open question 4). The design must not presume the answer.

## Decision
1. **The snapshot carries what the decision needs and nothing that is a secret.**
   - **Tags are named by `HMAC-SHA256(device key, UID)`,** with a key derived per device. A stolen terminal yields digests that only work for it, and a 56-bit UID cannot be recovered from them.
   - **Wallets appear as a status word and an allowance:** `min(balance, cap)`, with a 5,000 EGP cap by default. That is enough to decide a capture, keeps the balance private, and bounds what one terminal can overspend.
   - **PIN hashes are not included.** A 6-digit PIN falls to an offline brute force on any device that holds its hash. Offline, only an agent already signed in on that terminal keeps working until the session ends; a new sign-in needs the network until the Kotlin POS has secure hardware (B8).
2. **The terminal decides with the server's rules, in the server's order,** on the snapshot plus its own queued captures (provisional decrement: cooldown, limits and allowance count what it has already approved). The queue is in IndexedDB, so a reload or a crash loses nothing.
3. **The server always records an offline capture.** It runs the same engine at the capture's own time (`deviceAt`), with the price in force then. The fueling is `COMPLETED` (`origin = OFFLINE`); every check that fails is stored in `conflicts` and raises one `SYNC_CONFLICT` alert. Fuel dispensed at the pump is never reversed against the agent or the driver (PRD §7.5).
4. **What the wallet cannot cover becomes a receivable, not a negative wallet.**
   - The station payable is credited in full, so the station is always paid for fuel it gave.
   - The wallet is debited what it holds; the rest goes to `company:{id}:receivable`.
   - The wallet's `CHECK >= 0` stands. The ledger records the debt exactly. Whatever the owner decides (the company repays it from its next top-up, or the platform writes it off), it is one later posting against that account, and nothing recorded now has to change.
5. **An offline capture is tied to a person by session history.** The item names its agent. The server accepts it only if that agent had a session on this terminal open at `deviceAt`; otherwise the item is `REJECTED` (`AGENT_NOT_ON_DEVICE`).
6. **Batches are idempotent per item.**
   - Each item is its own transaction, keyed by `clientTxnId` as online.
   - A re-sent batch answers `DUPLICATE` for every item and moves no money.
   - A capture that fell back to the queue after an online attempt that did reach the server is recognised the same way.
   - A capture the server cannot record (unknown tag) is `REJECTED` and raises `SYNC_REJECTED`, which carries the UID's last four characters only.
7. **A contract version** (`X-Zawed-Contract`) lets the server refuse a terminal too old to speak the protocol (`426`) before it syncs.

## Consequences
- **The sync monitor shows each terminal's last contact and the count it still holds,** so a terminal that cannot sync is visible before its queue grows old.
- **Captures older than 7 days are still recorded,** flagged `STALE_CAPTURE`.
- **A revoked terminal cannot sync.** Its queue stays on the device, which keeps the snapshot cleared, for Operations to recover by hand: fuel was dispensed, and dropping it silently would hide it.
- **Receivables are reported in B6** (the company statement), together with the owner's settlement rule.
- **The full snapshot is sent each time,** about 200 bytes per tag. Deltas are a B7 optimisation if the network shows it is needed.
