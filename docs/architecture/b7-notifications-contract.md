# B7 W5: in-app notifications contract

**Status:** built on `feature/b7-w5-notifications` (2026-11-19), not merged. **Owner decision (2026-11-19):** notifications are in-app only; e-mail comes later.
**Code:** module `notifications` (`apps/api/src/main/java/com/zawed/api/notifications`), migration `V19__notifications.sql`. The web bell and list are built later from this contract (`apps/web/src/api/generated/schema.d.ts` is current).

## 1. How a notification is made

1. A producer module publishes its own domain event (a public record in its package) with Spring's `ApplicationEventPublisher`, inside the transaction of the change. Producers do not depend on `notifications`.
2. `notifications.NotificationTriggers` hears it with a plain `@EventListener`, synchronously, in that same transaction, and writes one row through `Notifications.send` (`Propagation.MANDATORY`).
3. So **a notification exists if and only if its cause committed**: a rolled-back cause leaves nothing, and an event published outside a transaction is refused (`IllegalTransactionStateException`), never written without a scope.

Why not `@TransactionalEventListener(BEFORE_COMMIT)`: it also writes in the transaction, but it is silently skipped when there is no transaction and would survive a rolled-back savepoint. The plain listener is the convention `fueling.Alerts` and `ledger.Wallets` already follow. The cost is accepted: if the notification insert fails, its cause fails too.

A notification carries **no display text**. It has a `type`, the entity it points at, and `params` that are only UUIDs, integers (piastres, millilitres) and codes (enum names). No name, plate, bank reference, account digits, reason, subject or message body is copied. The web writes the sentence from its translation keys (Arabic and English) and the params. The database refuses anything else (`notification_params_valid`, a CHECK constraint).

## 2. Events, audiences and parameters

An audience is a scope (the platform, one company or one operator) and the roles in it. Everyone who holds one of those roles in that scope sees the notification. **The user whose action caused it does not see it** (`actor_id`): an Ops Manager who verified a top-up cannot approve it, so a "waiting for your approval" notice would only be noise.

| `type` | Cause (where it is published) | Audience | `entityType` / `entityId` | `params` |
|---|---|---|---|---|
| `WALLET_LOW_BALANCE` | A posting takes the wallet from at or above its threshold to below it (`ledger.LedgerStore`, event `WalletLowBalance`) | the company: `FLEET_MANAGER`, `ACCOUNTANT` | `COMPANY_WALLET` / company id | `balancePiastres`, `thresholdPiastres`, `transactionId` |
| `TOPUP_CREDITED` | A top-up is approved and credits the wallet, once (`ledger.TopUps.approve`, `TopUpCredited`) | the company: `FLEET_MANAGER`, `ACCOUNTANT` | `TOPUP` / top-up id | `amountPiastres` (verified, credited), `transactionId` |
| `TOPUP_REJECTED` | Operations rejects a top-up **the company requested** (`ledger.TopUps.reject`, `TopUpRejected`) | the company: `FLEET_MANAGER`, `ACCOUNTANT` | `TOPUP` / top-up id | `amountPiastres` (claimed) |
| `TICKET_ANSWERED` | Operations writes a public answer; an internal note is not one (`tickets.Tickets.reply`, `TicketAnswered`) | the ticket's side: the company (`FLEET_MANAGER`, `ACCOUNTANT`) or the operator (`STATION_MANAGER`) | `TICKET` / ticket id | `ticketType` |
| `TOPUP_AWAITING_APPROVAL` | A top-up reaches `PENDING_APPROVAL`: verified, or recorded by Operations (`ledger.TopUps.verify`, `recordTransfer`; `TopUpAwaitingApproval`) | the platform: `OPS_MANAGER` | `TOPUP` / top-up id | `companyId`, `amountPiastres` (verified) |
| `WALLET_ADJUSTMENT_AWAITING_APPROVAL` | An adjustment is requested; an Idempotency-Key replay is not (`ledger.WalletAdjustments.request`, `WalletAdjustmentAwaitingApproval`) | the platform: `OPS_MANAGER` | `WALLET_ADJUSTMENT` / adjustment id | `companyId`, `amountPiastres` |
| `HARD_ALERT_RAISED` | Any hard alert is written; a repeated sync rejection is not (`fueling.Alerts.raise`, `HardAlertRaised`) | the platform: `OPS`, `OPS_MANAGER` | `ALERT` / alert id | `alertType`, `severity`, and when they apply `companyId`, `operatorId`, `stationId`, `fuelingId` |
| `TICKET_OPENED` | A company or station opens a ticket (`tickets.Tickets.open`, `TicketOpened`) | the platform: `OPS`, `OPS_MANAGER` | `TICKET` / ticket id | `ticketType`, and `companyId` or `operatorId` |
| `PAYOUT_POSTED` | A payout posts: at once at or below the threshold, or when approved (`stationfinance.Payouts`, `PayoutPosted`) | the operator: `STATION_MANAGER` | `PAYOUT` / payout id | `stationId`, `amountPiastres` |
| `BANK_ACCOUNT_CHANGE_APPROVED` | A bank account change is approved (`stationfinance.BankAccounts.approve`, `BankAccountChangeDecided`) | the operator: `STATION_MANAGER` | `STATION_BANK_ACCOUNT` / change id | `stationId` |
| `BANK_ACCOUNT_CHANGE_REJECTED` | A bank account change is rejected (`stationfinance.BankAccounts.reject`, `BankAccountChangeDecided`) | the operator: `STATION_MANAGER` | `STATION_BANK_ACCOUNT` / change id | `stationId` |

Codes in `params`: `alertType` is an `alerts.type` (`TANK_CAPACITY_EXCEEDED`, `VIP_CEILING_PASSED`, `LOW_BALANCE`, `SYNC_CONFLICT`, `SYNC_REJECTED`, `OFFLINE_EXPOSURE`, `OUTCOME_MISMATCH`, `COMPANY_OFFLINE_EXPOSURE`), `severity` is `WARNING` or `CRITICAL`, `ticketType` is a `tickets.type`. A new key or code is a contract change: add it here and in both languages of the web.

A low balance raises both `WALLET_LOW_BALANCE` (for the company) and, through its `LOW_BALANCE` alert, `HARD_ALERT_RAISED` (for Operations).

## 3. Endpoints

Shared by every portal, under `/api/v1/notifications`. Like `/auth/me`, they need a signed-in user and no permission: the caller's scope and role decide what is in the feed (the service filters, row-level security filters again). A notification outside them answers **404**.

| Method and path | Answer |
|---|---|
| `GET /api/v1/notifications?unread=&cursor=&limit=` | `Page<NotificationResponse>`, newest first (UUIDv7 id order). `unread=true` returns only the unread ones. `limit` 1–100, default 25; a bad cursor is `400 INVALID_CURSOR`. |
| `GET /api/v1/notifications/unread-count` | `{ "count": 0–99, "capped": bool }`. `capped` is true above 99 (show "99+"); counting stops there so a poll never reads every unread row. |
| `POST /api/v1/notifications/{id}/read` | `204`. Marking it again changes nothing. Someone else's, another tenant's or an unknown id: `404 NOT_FOUND`. |
| `POST /api/v1/notifications/read-all` | `204`. Marks every notification the caller can see now. |

`NotificationResponse`: `id`, `type`, `entityType`, `entityId`, `params` (object), `createdAt`, `read` (bool), `readAt` (null while unread). Read state is per user: one Fleet Manager reading a notice does not mark it read for the other Fleet Manager or the Accountant.

## 4. Database (`V19`)

- `notifications`: `id`, `type`, `audience_scope_type` (`PLATFORM` / `COMPANY` / `OPERATOR`), `audience_scope_id` (null for the platform), `audience_roles text[]` (only roles of that kind of scope), `entity_type`, `entity_id`, `params jsonb` (checked), `actor_id`, `created_at`. Index `(audience_scope_type, audience_scope_id, id)` for the feed and `(created_at)` for retention.
- `notification_reads`: `(user_id, notification_id)` primary key, `read_at`. The primary key answers "has this user read it"; deleting a notification cascades to its read rows.
- **Grants:** `zawed_app` has `SELECT, INSERT` on both tables and nothing else: a notification is never edited and a read is never unread.
- **Row-level security:** a user reads a notification when their role (`app_user_role()`, read from their own `users` row) is in `audience_roles`, the audience is their scope, and they are not its actor. A platform user does **not** read company or operator notifications. A write must carry the transaction's own user as actor; the platform may address anyone; a company or operator (with a user) may address itself or the platform (a ticket it opened), never another tenant. A read row is only the user's own, and only for a notification they can see.
- **One writer:** only `notifications/Notifications.java` inserts into these tables (`ArchitectureTests.onlyTheNotificationsModuleWritesNotifications`).

## 5. Retention

`NotificationRetention` deletes notifications older than `zawed.notifications.retention` (default `P90D`, never under 30 days) every `zawed.notifications.purge-interval` (default `PT6H`, first run 10 minutes after start), in batches of 5,000, at most 40 batches a run. It calls `purge_notifications(before, limit)`, a SECURITY DEFINER function that refuses anything younger than 30 days and uses `SKIP LOCKED` so two instances share the work. This needs no platform-scope escalation and no `DELETE` grant.

## 6. Not in v1

- E-mail (owner decision 2026-11-19). When it comes, it is delivery work after commit (the event outbox), reading these same events.
- Notices nobody asked for yet: a payout, bank account change or reversal waiting for a second person; a company's reply on a ticket for Operations; a wallet adjustment decided; a company-visible alert for the company; a reconciliation finding.
- Push or live updates: the web polls `unread-count`.

## 7. Tests

`notifications.NotificationsTests` (audiences for tickets, top-ups and adjustments, read state, rollback, no transaction, row-level security, params check, retention), `fueling.FuelingNotificationsTests` (low balance, hard alerts), `fueling.StationFinanceNotificationsTests` (bank account decisions, payouts), `AuthorizationMatrixTests` (the four endpoints for every role), `ArchitectureTests`, `OpenApiSnapshotTests`.
