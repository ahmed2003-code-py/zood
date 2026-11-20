# B7 W5: in-app notifications contract

**Status:** built on `feature/b7-w5-notifications` (2026-11-19); review findings M1, M2, L1, L2 and L4 fixed (2026-11-20), V19 edited in place (never deployed); not merged. **Owner decision (2026-11-19):** notifications are in-app only; e-mail comes later.
**Code:** module `notifications` (`apps/api/src/main/java/com/zawed/api/notifications`), migration `V19__notifications.sql`. The web bell and list are built later from this contract (`apps/web/src/api/generated/schema.d.ts` is current).

## 1. How a notification is made

1. A producer module publishes its own domain event (a public record in its package) with Spring's `ApplicationEventPublisher`, inside the transaction of the change. Producers do not depend on `notifications`.
2. `notifications.NotificationTriggers` hears it with a plain `@EventListener`, synchronously, in that same transaction, and writes one row through `Notifications.send` (`Propagation.MANDATORY`), inside a **savepoint** (a `NESTED` transaction template).
3. So **a notification never exists without its cause**: a rolled-back cause leaves nothing, and an event published outside a transaction is refused (`IllegalTransactionStateException`), never written without a scope.
4. And **a notification never stops its cause** (review finding M1). If writing it fails for any reason (building the notice, the per-type key check, row-level security, a constraint, a trigger), it is rolled back to the savepoint, counted in the metric **`zawed.notifications.dropped`** (tag `type`; one counter per type, registered at start so it reads 0 until something is dropped) and logged as an error, and the cause commits. A fueling or offline sync item is still recorded with its posting, balance, cooldown and alerts; a rejected sync item is still answered as rejected and its `SYNC_REJECTED` alert kept.

The same rule applies to every cause, not only fuelings: a top-up verification or approval, a wallet adjustment request, a ticket, a payout and a bank account decision are facts with their own screens and queues, and the notice is only a pointer to them. Failing them over a notice would make a deterministic notification bug block every approval of that kind; a dropped notice is visible on the metric and can be rebuilt from its cause. Alert on `zawed.notifications.dropped` > 0.

Why not `@TransactionalEventListener(BEFORE_COMMIT)`: it also writes in the transaction, but it is silently skipped when there is no transaction and would survive a rolled-back savepoint. The plain listener is the convention `fueling.Alerts` and `ledger.Wallets` already follow.

A notification carries **no display text**. It has a `type`, the entity it points at, and `params` that are only UUIDs, integers (piastres, millilitres) and codes (enum names), and **only the keys listed for its type** in §2 (review finding L2; a key that does not apply is left out, never replaced by another). No name, plate, bank reference, account digits, reason, subject or message body is copied. The web writes the sentence from its translation keys (Arabic and English) and the params. `Notifications.Type` checks the keys before the insert, and the database refuses anything else (`notification_param_keys(type)` and `notification_params_valid(type, params)`, a CHECK constraint); a test keeps the two lists equal.

## 2. Events, audiences and parameters

An audience is a scope (the platform, one company or one operator) and the roles in it. Everyone who holds one of those roles in that scope sees the notification. **The user whose action caused it does not see it** (`actor_id`): an Ops Manager who verified a top-up cannot approve it, so a "waiting for your approval" notice would only be noise.

| `type` | Cause (where it is published) | Audience | `entityType` / `entityId` | `params` (the only keys allowed) |
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
| `GET /api/v1/notifications?unread=&cursor=&limit=` | `Page<NotificationResponse>`, newest first by `(createdAt, id)`. `unread=true` returns only the unread ones. `limit` 1–100, default 25. The cursor is opaque (today `<createdAt epoch µs>.<id>`): pass `nextCursor` back as it came. Anything else, an id alone included, is `400 INVALID_CURSOR`. |
| `GET /api/v1/notifications/unread-count` | `{ "count": 0–99, "capped": bool }`. `capped` is true above 99 (show "99+"); counting stops there so a poll never reads every unread row. |
| `POST /api/v1/notifications/{id}/read` | `204`. Marking it again changes nothing. Someone else's, another tenant's or an unknown id: `404 NOT_FOUND`. |
| `POST /api/v1/notifications/read-all` | `204`. Marks every notification the caller can see now. |

`NotificationResponse`: `id`, `type`, `entityType`, `entityId`, `params` (object), `createdAt`, `read` (bool), `readAt` (null while unread; for one covered by "mark all read", the time of that click). Read state is per user: one Fleet Manager reading a notice does not mark it read for the other Fleet Manager or the Accountant.

**Paging (review finding L4).** The feed is a keyset on `(created_at, id)`, descending: the next page is `(created_at, id) < (cursor)`, a row comparison that is an index condition on `notifications_audience_idx`. Rows with the same `created_at` are ordered by id, so a page boundary inside a tie neither skips nor repeats a row, and a cursor stays valid after the row it names is purged.

**Unread (review finding M2).** Each user has at most one mark, `notification_read_marks.read_up_to`. Unread is "addressed to me, created after my mark, and not read one by one". The count is an index range from the mark onward (on `notifications_audience_idx`, or on the purge's `(created_at)` index when the planner prefers it; the mark is read first and bound as a parameter, because inline under `coalesce` PostgreSQL cannot prove it leakproof and row-level security would keep it a filter), so it reads what arrived since the user last pressed "mark all read", not the platform's whole feed (Operations shares one platform feed of every hard alert, ticket and approval). "Mark all read" writes the mark and at most a few read rows, never a row per notification:
- the mark moves to **5 minutes before the click** (`Notifications.MARK_GRACE`), forward only (`greatest`, and a trigger refuses a move back);
- what the user can see inside those 5 minutes gets ordinary read rows.

The gap is there because a notification is stamped when inserted but visible only when its transaction commits. One inserted just before the click and committed just after it was never seen, and a mark at the click would cover it. Every transaction that writes a notification (one fueling, one sync item, one approval step) is far shorter than 5 minutes, which also absorbs clock differences between API instances. The mark is a timestamp, not a transaction id or snapshot, because those do not survive the `pg_dump` restore in `docs/runbooks/backup-and-restore.md`. Two clicks at once queue on the mark's row lock, so their read rows cannot deadlock.

## 4. Database (`V19`)

- `notifications`: `id`, `type`, `audience_scope_type` (`PLATFORM` / `COMPANY` / `OPERATOR`), `audience_scope_id` (null for the platform), `audience_roles text[]` (only roles of that kind of scope), `entity_type`, `entity_id`, `params jsonb` (the type's keys only, checked), `actor_id`, `created_at`. Index `(audience_scope_type, audience_scope_id, created_at, id)` for the feed's keyset and the unread range, and `(created_at)` for retention.
- `notification_reads`: `(user_id, notification_id)` primary key, `read_at`. The primary key answers "has this user read it"; deleting a notification cascades to its read rows.
- `notification_read_marks`: `user_id` primary key, `read_up_to`, `marked_at`. A trigger refuses a mark that moves back or changes user.
- **Grants:** `zawed_app` has `SELECT, INSERT` on `notifications` and `notification_reads` and nothing else: a notification is never edited and a read is never unread. On `notification_read_marks` it has `SELECT, INSERT, UPDATE` (the mark moves forward in place), never `DELETE`.
- **Row-level security:** a user reads a notification when their role (`app_user_role()`, read from their own `users` row) is in `audience_roles`, the audience is their scope, and they are not its actor. A platform user does **not** read company or operator notifications. A write must carry the transaction's own user as actor. A platform session **with** a user may address anyone; a platform session **without** a user (a POS device, a job) may write only `WALLET_LOW_BALANCE` and `HARD_ALERT_RAISED`, the only things it causes (review finding L1). A company or operator (with a user) may address itself or the platform (a ticket it opened), never another tenant. A read row and a mark are only the user's own, and a read row only for a notification they can see.
- **One writer:** only the `notifications` module writes these tables: `Notifications.java` (inserts, the mark) and `NotificationRetention.java` (the purge function). `ArchitectureTests.onlyTheNotificationsModuleWritesNotifications` looks for any insert, update, delete, merge, copy or truncate on them, with or without a schema or quotes, and any call to `purge_notifications`.

## 5. Retention

`NotificationRetention` deletes notifications older than `zawed.notifications.retention` (default `P90D`, never under 30 days) every `zawed.notifications.purge-interval` (default `PT6H`, first run 10 minutes after start), in batches of 5,000, at most 40 batches a run. It calls `purge_notifications(before, limit)`, a SECURITY DEFINER function that refuses anything younger than 30 days and uses `SKIP LOCKED` so two instances share the work. This needs no platform-scope escalation and no `DELETE` grant.

## 6. Not in v1

- E-mail (owner decision 2026-11-19). When it comes, it is delivery work after commit (the event outbox), reading these same events.
- Notices nobody asked for yet: a payout, bank account change or reversal waiting for a second person; a company's reply on a ticket for Operations; a wallet adjustment decided; a company-visible alert for the company; a reconciliation finding.
- Push or live updates: the web polls `unread-count`.

## 7. Tests

`notifications.NotificationsTests` (audiences for tickets, top-ups and adjustments, read state, rollback, no transaction, row-level security, params check, retention), `fueling.FuelingNotificationsTests` (low balance, hard alerts), `fueling.StationFinanceNotificationsTests` (bank account decisions, payouts), `AuthorizationMatrixTests` (the four endpoints for every role), `ArchitectureTests`, `OpenApiSnapshotTests`.

Review findings (2026-11-20):
- **M1:** `fueling.NotificationFailureTests` (an online fueling and an offline batch, with a recorded and a rejected item, while the database refuses their notices; the metric counts 5) and `NotificationWriteRulesTests.aTopUpIsVerifiedAndCreditedAndATicketOpenedWhenTheirNotificationsAreRefused`.
- **M2:** `NotificationReadMarkTests`: one mark instead of a row per notification; a notification committed during the click stays unread; 8 concurrent clicks; a mark is its own user's and never moves back; the unread count and the feed are index ranges, as read from the plan of the application's own statements under row-level security.
- **L1:** `NotificationWriteRulesTests.aUserlessPlatformSessionAnnouncesOnlyALowBalanceOrAHardAlert`, and the wider `ArchitectureTests` pattern.
- **L2:** `NotificationWriteRulesTests` (the Java and database key lists are equal, every type with all its keys and with none, another key refused, a bad key in Java dropped and counted while the transaction goes on) and `NotificationsTests.aNotificationCarriesIdsNumbersAndCodesOnly`.
- **L4:** `NotificationReadMarkTests.pagingByTimeAndIdNeitherSkipsNorRepeatsRowsThatShareATime` and `aCursorThatIsNotAKeysetIsABadRequest`.
