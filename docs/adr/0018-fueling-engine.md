# ADR 0018: The online fueling engine and the POS chain

**Status:** Accepted, 2026-11-13
**Source:** `docs/architecture/implementation-plan.md` §3.4 and §7 B4; `docs/architecture/b4-fueling-contract.md`; ADR 0010 (device channel)

## Context
B4 is the first code where a device, not a user, spends a company's money. Plan §3.4 fixes the steps. Left open: how a request with no user passes row-level security, how a decline and a posting fit one transaction without the ledger ever refusing, what a fueling row carries so that two audiences can read it, how retries are recognised, and how a device credential is kept apart from a user's.

## Decision
1. **A separate security chain for `/api/v1/pos/**`**, ordered before the users' chain. Its only way in is `PosAuthenticator` (declared in `platform`, implemented in `network`, so platform code does not depend on a business module). Authorities `POS_DEVICE`, `POS_AGENT` and `POS_AGENT_PIN_CHANGE` decide the routes; a refusal names the next step (`AGENT_SESSION_REQUIRED`, `PIN_CHANGE_REQUIRED`). Device tokens are signed with a key derived from the JWT secret under their own label and carry `typ=device`, so a device token fails a user token's signature check before any type check, and the reverse.
2. **The platform scope, entered explicitly and in a closed list of places.** A device has no tenant. The device channel, agent sessions, the engine and the receipt read call `DbScope.runAs(PLATFORM)` once their proof is checked (activation code, signature, verified device and agent, link signature). What the engine writes is bounded by facts it did not take from the request: the station is the device's, the company is the tag's. `ArchitectureTests` fails when a new class calls `runAs`. Rejected: an `OPERATOR` scope for the device, because the engine must read the company's wallet, vehicle and limits, which RLS hides from an operator; and wider RLS policies for devices, which would widen every table for every caller (B3 review finding 3).
3. **Decide, then write; the ledger never refuses.** The engine locks the wallet, then the tag, reads the facts, decides with the pure `FuelingRules`, and only then posts and writes. The balance check is the decision's last step under the wallet lock, so `ledger.post` cannot answer `INSUFFICIENT_BALANCE` and a decline never needs a rollback (B3 review finding 2). The posting comes before the fueling row because the row's `CHECK` ties `ledger_transaction_id` to "completed, billable, amount > 0" and the row is never updated.
4. **The wallet lock serialises a company's fuelings.** Limits are counted from completed fuelings with a plain `sum` under that lock, so no counter table and no race. The cost is that one company's pumps are served one at a time. A fueling is milliseconds inside the lock, far below a company's real fueling rate.
5. **Idempotency on `clientTxnId`, checked twice.** The engine looks the id up before and again after taking the wallet lock. The unique index is the last word: a concurrent duplicate rolls back entirely, ledger posting included, and the stored outcome is returned. The same id from another device, or for another tag or volume, is a client error rather than a silent replay.
6. **The fueling row is the receipt.** It snapshots the plate, the company and station names and the agent's worker ID. A company cannot read stations and an operator cannot read companies under RLS, and a receipt must not change when master data is renamed.
7. **Alerts in the same transaction.** Tank capacity and the VIP ceiling are raised by the engine. Low balance is raised by an `@EventListener` on the ledger's `WalletLowBalance`, which is published inside the posting. An alert therefore never exists without its fueling, and the reverse.
8. **Receipt links are keyed signatures**, `HMAC(server key, "receipt-link", fuelingId)` in hex. There is no table and no expiry, and the link opens one receipt with no ids.

## Consequences
- B5 offline ingest reuses the engine with `origin = OFFLINE` and the device time as the evaluation time. It must decide what replaces `INSUFFICIENT_BALANCE` while the wallet's `CHECK >= 0` stands (ADR 0017, consequence 3).
- A second engine instance needs nothing extra: locks and unique indexes are in PostgreSQL.
- Adding a POS route means adding it to the chain's rules and to the authorization matrix (`DEVICE` rows), or it is refused.
- B2 had given the activation code 72 hours; B4 brings it to the 15 minutes ADR 0010 sets: Ops issues it while the installer is at the device.
