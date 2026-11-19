# ZAWED database: entity–relationship guide

**Schema version:** 16 (`V1` … `V16` in `apps/api/src/main/resources/db/migration`) · **Database:** PostgreSQL 18 · **Updated:** 2026-11-19 (production hardening)

This guide explains every table, grouped by business area. Each section has a diagram (Mermaid; it renders on GitHub and in VS Code), what the tables are for, and the rules the database itself enforces. The migrations are the source of truth: when this guide and a migration disagree, the migration wins, and the guide should be fixed.

**Contents**
1. Conventions that apply to every table
2. The map: how the areas connect
3. Multi-tenancy: how each customer is kept apart
4. Identity and audit
5. Companies and fleet
6. Station network and devices
7. Fuel prices
8. Ledger, wallets and reconciliation
9. Fuelings, alerts and offline sync
10. Station finance
11. Support tickets
11b. Bulk vehicle import
12. Who can see what (row-level security)
13. How money moves (ledger postings)
14. Migration history

---

## 1. Conventions that apply to every table

| Rule | Detail |
|---|---|
| Keys | `uuid` version 7, created by the application, or by an offline device for `fuelings.client_txn_id`. Time-ordered, so `ORDER BY id` is creation order and lists page by `id < :cursor`. |
| Money | `bigint` **piastres** (1 EGP = 100 piastres). Never `numeric`, `real` or `double`. Columns end in `_piastres`. |
| Volume | `bigint` **millilitres**. Columns end in `_ml`. |
| Time | `timestamptz` holding UTC. Shown in `Africa/Cairo` by the clients. Limit periods use the station's time zone; weeks start on Saturday. |
| Nothing is deleted | "Deleted" is a status (`DEACTIVATED`, `ARCHIVED`, `CLOSED`, `SUPERSEDED`…). Fuelings, ledger entries, audit rows and ticket messages are append-only, and triggers refuse `UPDATE`/`DELETE` on them. |
| Foreign keys | Every FK has an index. Columns named `*_by` (`created_by`, `decided_by`…) hold a user id but are **deliberately not FKs**: an audit or decision row must never be refused because of another table. |
| Audit columns | Most master tables have `created_at`, `created_by`, `updated_at`, `updated_by`. The diagrams leave them out to stay readable. |
| Enumerations | `text` with a `CHECK (… IN (…))`, mirrored by a Java enum. Adding a value means a new migration that drops and re-creates the check. |
| Migrations | Forward only. A mistake is fixed by a new migration, never by editing an applied one. |
| Runtime role | The application connects as the owner but runs every transaction as `zawed_app` (`SET ROLE`). `zawed_app` has only the grants each table needs, and row-level security applies to it (section 11). |

---

## 2. The map: how the areas connect

```mermaid
flowchart LR
    subgraph ID[Identity and audit]
        users --- sessions
        users --- mfa_challenges
        audit_log
    end
    subgraph FLEET[Companies and fleet]
        companies --> departments
        companies --> vehicles --> rfid_tags
        vehicles --> limit_rules
    end
    subgraph NET[Station network]
        station_operators --> stations --> pos_devices
        stations --> agents
        agents --> agent_sessions
        pos_devices --> device_nonces
        fuel_prices
    end
    subgraph LEDGER[Ledger and wallets]
        ledger_accounts --> ledger_entries
        ledger_transactions --> ledger_entries
        company_wallets
        topup_requests
        reconciliation_runs --> reconciliation_findings
    end
    subgraph FUEL[Fueling and sync]
        fuelings
        alerts
        sync_batches
    end
    subgraph FIN[Station finance]
        station_payables
        station_bank_accounts --> payouts
        payable_hold_releases
        fueling_reversals
    end
    subgraph SUP[Tickets]
        tickets --> ticket_messages
    end

    users -. "scope_id = company or operator" .-> companies
    users -. scope_id .-> station_operators
    rfid_tags --> fuelings
    pos_devices --> fuelings
    agents --> fuelings
    fuelings --> ledger_transactions
    companies --> company_wallets
    stations --> station_payables
    fuelings --> fueling_reversals
    fuelings --> payable_hold_releases
    pos_devices --> sync_batches
    companies --> tickets
    station_operators --> tickets
```

A fueling joins the two sides of the business. **Fleet side:** the company, vehicle and tag. **Station side:** the operator, station, device and agent. A completed fueling posts one ledger transaction that moves money from the company's wallet to the station's payable.

---

## 3. Multi-tenancy: how each customer is kept apart

**The model: one database, one schema, a tenant column on every row, and row-level security.** There is no database or schema per customer. Isolation is enforced by PostgreSQL on every query, and checked again in the API.

| Scope | Who | Tenant column | Sees |
|---|---|---|---|
| `PLATFORM` | Super Admin, Ops | — | everything |
| `COMPANY` | Fleet Manager, Accountant | `company_id` | its own company's rows |
| `OPERATOR` | Station Manager | `operator_id` | its own operator's stations and their rows |

Tables fall into four groups:
- **Company-owned:** `companies`, `departments`, `vehicles`, `rfid_tags`, `limit_rules`, `company_wallets`, `topup_requests`.
- **Operator-owned:** `station_operators`, `stations`, `pos_devices`, `agents`, `agent_sessions`, `station_payables`, `station_bank_accounts`, `payouts`, `sync_batches`.
- **Shared between both sides:** these carry both `company_id` and `operator_id`, and each side sees its own: `fuelings`, `fueling_reversals`, `alerts`. `tickets` carries one or the other.
- **Global:** `fuel_prices`, read by everyone and written by the platform.

```mermaid
flowchart LR
    A["Request + session token<br/>scope = PLATFORM | COMPANY | OPERATOR"] --> B["@RequiresPermission<br/>role allowed?"]
    B --> C["Service<br/>record of this tenant?<br/>else 404"]
    C --> D["ScopedTransactionManager<br/>SET ROLE zawed_app<br/>set_config app.scope_*"]
    D --> E[("PostgreSQL<br/>RLS policies<br/>same-tenant triggers<br/>narrow grants")]
    P["POS device signature<br/>sign-in proof"] -. "DbScope.runAs(PLATFORM)<br/>closed list" .-> D
```

Layers, in the order a request meets them:

1. **The token decides the tenant, never the request.** The scope comes from the signed-in user's session. Endpoints under `/company/**` and `/station/**` take no company or operator id from the URL or the body. A request that names another tenant's record simply does not find it.
2. **Permission.** Each endpoint declares `@RequiresPermission`, and `AuthorizationMatrixTests` checks every endpoint against every role.
3. **Service checks.** Where a record is named by id (a station's statement, for example), the service checks it belongs to the caller (`StationAccess`) and answers **404**, never 403, so another tenant's ids are not even confirmed to exist.
4. **The transaction carries the scope.** `ScopedTransactionManager` starts every transaction with `set_config('app.scope_type' | 'app.scope_id' | 'app.user_id', …, true)`. These settings are transaction-local, so a pooled connection never carries one request's scope into the next. Every connection runs `SET ROLE zawed_app` (`connection-init-sql`), and a transaction running as any other role is refused. A query outside a transaction has no scope and sees no scoped rows: it fails safe.
5. **Row-level security** on every tenant table (V3, V4, V6–V11), using `app_scope_type()`, `app_scope_id()` and `app_is_platform()`. A bug in a query cannot return another tenant's rows.
6. **Same-tenant triggers.** `check_same_tenant` (V4) and `fuelings_same_tenant` (V7_1) refuse a row whose parents belong to different tenants, such as a tag on another company's vehicle or a fueling whose station is not the device's.
7. **Narrow grants.** `zawed_app` has only what each table needs; for example, no `UPDATE` or `DELETE` on `fuelings` or ledger entries.

**The only ways around it** are explicit `DbScope.runAs(PLATFORM)` calls. They happen only after a proof is checked, and only in a closed list that `ArchitectureTests` enforces:
- sign-in: `identity/Sessions`, `identity/SignIn`;
- the POS channel: `network/DeviceChannel`, `network/AgentSessions`;
- fueling: `fueling/FuelingEngine`, `fueling/Receipts`;
- offline: `fueling/OfflineSync`, `sync/Snapshots`.

Adding a file to that list needs a review.

**Tests that prove it:** `RowLevelSecurityTests`, `MasterDataIsolationTests`, `LedgerIsolationTests`, `AuthorizationMatrixTests`, `ArchitectureTests`, plus cross-tenant cases in `StationFinanceTests` and `TicketsTests`.

**Checklist for a new table that holds tenant data:**
1. A `company_id` and/or `operator_id` column, with an index.
2. `ENABLE ROW LEVEL SECURITY`, and a policy for each scope that reads it.
3. Grants to `zawed_app` for only what is needed.
4. A same-tenant trigger if it has a parent in a tenant.
5. Rows in `AuthorizationMatrixTests`, and an isolation test that one tenant cannot read another's rows.

---

## 4. Identity and audit (`V2`, `V3`; module `identity`, `audit`)

```mermaid
erDiagram
    users ||--o{ sessions : "signs in"
    users ||--o{ mfa_challenges : "second step"
    users ||..o{ audit_log : "actor_id (not an FK)"
    users ||..o{ idempotency_keys : "actor_id (not an FK)"

    users {
        uuid id PK
        text email UK "lower-cased, unique"
        text full_name
        text password_hash "Argon2id"
        text role "SUPER_ADMIN | OPS_MANAGER | OPS | FLEET_MANAGER | ACCOUNTANT | STATION_MANAGER"
        text scope_type "PLATFORM | COMPANY | OPERATOR"
        uuid scope_id "company or operator id; null for PLATFORM"
        text status "ACTIVE | SUSPENDED | DEACTIVATED"
        boolean must_change_password
        int failed_login_count
        timestamptz locked_until
        bytea mfa_secret_encrypted "AES-256-GCM"
        boolean mfa_enabled
        bigint mfa_last_used_step "a TOTP step is never accepted twice"
        timestamptz password_changed_at
    }
    sessions {
        uuid id PK
        uuid user_id FK
        text door "USER | ADMIN"
        text refresh_token_hash UK
        text previous_refresh_token_hash "reuse detection"
        timestamptz last_seen_at
        timestamptz expires_at "idle"
        timestamptz absolute_expires_at
        timestamptz revoked_at
        text revoked_reason
        text ip_prefix "/24 or /48 only"
    }
    mfa_challenges {
        uuid id PK
        uuid user_id FK
        text purpose "VERIFY | ENROLL"
        text door "USER | ADMIN"
        text token_hash UK
        text scope_type "snapshot of the user's scope"
        uuid scope_id
        bytea pending_secret_encrypted "during enrolment"
        int attempts
        timestamptz expires_at
        timestamptz used_at "single use"
    }
    idempotency_keys {
        uuid actor_id PK
        text operation PK "company.create, wallet_adjustment.create"
        text key PK "the Idempotency-Key header"
        text request_hash "SHA-256 of the body"
        uuid resource_id "what the first request created"
        timestamptz created_at
    }
    audit_log {
        uuid id PK
        timestamptz occurred_at
        uuid actor_id
        text actor_role
        text action "e.g. PAYOUT_APPROVED"
        text entity_type
        uuid entity_id
        text scope_type
        uuid scope_id
        jsonb metadata
        text request_id
        text ip_prefix
    }
```

- **The role decides the scope** (a CHECK): Super Admin, Ops Manager (V12) and Ops are `PLATFORM`; Fleet Manager and Accountant are `COMPANY` (`scope_id` = a company); Station Manager is `OPERATOR` (`scope_id` = a station operator). Station **agents** and **drivers** are not users: agents live in `agents` (section 5), and drivers do not exist yet.
- **MFA** is required for Super Admin, Ops and, since B6, Station Manager.
- **Sessions** are checked on every request. A refresh token is rotated on each use, and reusing an old one revokes the session.
- **`audit_log`** is written in the same transaction as the change it records, and is never updated or deleted.
- **`idempotency_keys`** (V13, ADR 0022): a create sent with an `Idempotency-Key` records the key, the body's hash and the resource, in the create's own transaction. A repeat with the same body returns the same resource; with another body it is refused. Each user sees only their own keys (RLS).
- **Security-definer functions** in `V3` (`auth_login_lookup`, `auth_session_by_id`, …) let sign-in read a user before any scope exists. They are the only way around row-level security for identity.

---

## 5. Companies and fleet (`V4`; module `tenancy`, `fleet`)

```mermaid
erDiagram
    companies ||--o{ departments : has
    companies ||--o{ vehicles : owns
    departments |o--o{ vehicles : "optional grouping"
    vehicles ||--o{ rfid_tags : "tag history"
    vehicles ||--o{ limit_rules : "spending limits"

    companies {
        uuid id PK
        text legal_name
        text name_ar
        text name_en
        text tax_id
        text commercial_registration
        text contact_name
        text contact_phone
        text contact_email
        text status "ACTIVE | FROZEN"
        int cooldown_minutes "120..240 between fuelings of one tag"
        bigint low_balance_threshold_piastres
    }
    departments {
        uuid id PK
        uuid company_id FK
        text name
        bigint monthly_budget_piastres "informational"
        text status "ACTIVE | ARCHIVED"
    }
    vehicles {
        uuid id PK
        uuid company_id FK
        uuid department_id FK
        text plate "as registered"
        text plate_key UK "canonical (V14); unique among non-decommissioned"
        text make
        text model
        int year
        text fuel_type "GAS_80 | GAS_92 | GAS_95 | DIESEL | CNG"
        bigint tank_capacity_ml "at most 1,500,000 (V14)"
        text tier "NORMAL | VIP"
        bigint vip_monthly_ceiling_ml "VIP only"
        text status "ACTIVE | SUSPENDED | DECOMMISSIONED"
        text fleet_code "V14: the company's own number; unique per company in service"
    }
    rfid_tags {
        uuid id PK
        uuid company_id FK "copied from the vehicle"
        uuid vehicle_id FK
        bytea uid_encrypted "the tag UID, encrypted"
        text uid_lookup UK "keyed hash for lookup"
        text uid_last4 "for display"
        text status "ACTIVE | LOST | STOLEN | DEACTIVATED | REPLACEMENT_PENDING"
        text status_reason
        timestamptz cooldown_until "next fueling allowed"
        text last_write_status "OK | FAILED"
    }
    limit_rules {
        uuid id PK
        uuid company_id FK
        uuid vehicle_id FK
        text kind "VOLUME | AMOUNT"
        text period "DAY | WEEK | MONTH"
        bigint max_value "ml or piastres"
        boolean active
        timestamptz deactivated_at
    }
```

- **A company is the tenant.** Every fleet row carries `company_id`, and the trigger `vehicles_same_tenant` (with similar ones on tags and limits) refuses a row whose company disagrees with its parent's.
- **Tag UIDs are never stored in clear text.** `uid_lookup` is a keyed hash used to find the tag that was read, `uid_encrypted` is kept for Ops, and only `uid_last4` is ever shown. A tag reported `LOST` or `STOLEN` stops working on the next fueling.
- **Limits** are per vehicle. A fueling must fit every active rule. Changing a rule deactivates the old row and inserts a new one, so history is kept.
- **Cooldown** lives on the tag (`cooldown_until`), set by each completed fueling to *now + the company's cooldown*.
- **Identity never changes (V14, ADR 0023).** A trigger refuses any change to a tag's `company_id`, `vehicle_id` or UID columns, and to a vehicle's `company_id`, for every role. With the UID unique for ever, each `rfid_tags` row is one assignment and the table is the assignment history.
- **Plate key (V14).** For the Egyptian shape (one digit group, one letter group) the key puts the digits first, so `١٢٣٤ ط ب ع` and `ط ب ع 1234` are one plate; any other shape keeps its order, so two different plates never share a key. The migration stops if existing vehicles would collide.

---

## 6. Station network and devices (`V4`, `V7`, `V10`; module `network`)

```mermaid
erDiagram
    station_operators ||--o{ stations : runs
    stations ||--o{ pos_devices : "has terminals"
    stations ||--o{ agents : employs
    pos_devices ||--o{ device_nonces : "used nonces"
    agents ||--o{ agent_sessions : "shift on a device"
    pos_devices ||--o{ agent_sessions : ""

    station_operators {
        uuid id PK
        text legal_name
        text name_ar
        text name_en
        text tax_id
        text commercial_registration
        text contact_name
        text contact_phone
        text contact_email
        text status "ACTIVE | SUSPENDED"
    }
    stations {
        uuid id PK
        uuid operator_id FK
        text name_ar
        text name_en
        text address
        numeric latitude
        numeric longitude
        text time_zone "e.g. Africa/Cairo"
        text connectivity "NORMAL | LOW"
        text_array fuel_types "fuels sold"
        text status "ACTIVE | SUSPENDED | CLOSED"
    }
    pos_devices {
        uuid id PK
        uuid operator_id FK
        uuid station_id FK
        text code UK "printed on the device, e.g. POS-7K2M"
        text label
        text status "PENDING_ACTIVATION | ACTIVE | REVOKED"
        text activation_code_hash "one-time, 15 min"
        timestamptz activation_expires_at
        int activation_attempts "max 5"
        bytea public_key "EC P-256 SPKI, bound at activation"
        text key_algorithm "EC_P256"
        timestamptz bound_at
        timestamptz last_sync_at
        timestamptz revoked_at
        text revoked_reason
    }
    agents {
        uuid id PK
        uuid operator_id FK
        uuid station_id FK
        text worker_id UK "printed on the staff card"
        text full_name
        text phone
        text pin_hash "Argon2id, 6 digits"
        boolean must_change_pin
        int failed_pin_count
        timestamptz locked_until "5 wrong PINs = 15 min"
        text status "ACTIVE | SUSPENDED | DEACTIVATED"
    }
    device_nonces {
        uuid device_id PK, FK
        text nonce PK "never accepted twice"
        timestamptz created_at
    }
    agent_sessions {
        uuid id PK
        uuid agent_id FK
        uuid device_id FK
        uuid operator_id FK
        uuid station_id FK
        text token_hash "SHA-256 of the X-Agent-Session token"
        timestamptz created_at "shift start"
        timestamptz last_seen_at
        timestamptz expires_at "12 h"
        timestamptz revoked_at "shift end"
        text revoke_reason
    }
```

- **The operator is the station-side tenant.** Station managers are scoped to one operator and see all its stations.
- **A device proves itself with its key pair** (ADR 0010). Ops registers it and reads a one-time activation code to the installer. The device sends its public key once, then signs a challenge every 15 minutes to get a token. Used nonces are kept to stop replays.
- **An agent session is a shift.** One live session per agent. Since `V10` the station manager can read the operator's sessions; that is the "Shifts" view and the "last shift" filter.

---

## 7. Fuel prices (`V4`; module `pricing`)

```mermaid
erDiagram
    stations |o--o{ fuel_prices : "station-specific price"
    fuel_prices {
        uuid id PK
        text fuel_type
        uuid station_id FK "null = national price"
        bigint price_piastres "per litre"
        timestamptz effective_from "scheduled ahead"
        text note
    }
```

Prices are **effective-dated and append-only**. The price in force for a station and a fuel at instant *t* is the latest row with `effective_from <= t`. A station-specific row beats the national row (`station_id IS NULL`). Only Operations sets prices. Devices receive the current price and the next 7 days of changes in the offline snapshot.

---

## 8. Ledger, wallets and reconciliation (`V6`, `V6_1`, `V8`, `V9`; module `ledger`)

```mermaid
erDiagram
    ledger_transactions ||--|{ ledger_entries : "2+ balanced lines"
    ledger_accounts ||--o{ ledger_entries : ""
    ledger_transactions |o--o| ledger_transactions : "reverses_transaction_id"
    companies ||--|| company_wallets : ""
    company_wallets ||--|| ledger_accounts : "COMPANY_WALLET account"
    stations ||--|| station_payables : ""
    station_payables ||--|| ledger_accounts : "STATION_PAYABLE account"
    companies ||--o{ topup_requests : asks
    companies ||--o{ wallet_adjustments : "credited by"
    wallet_adjustments |o--o| ledger_transactions : "when approved"
    topup_requests |o--o| ledger_transactions : "when confirmed"
    reconciliation_runs ||--o{ reconciliation_findings : finds

    ledger_accounts {
        uuid id PK
        text code UK "platform:bank, platform:adjustments, company:{id}:wallet, company:{id}:receivable, station:{id}:payable"
        text kind "PLATFORM_BANK | PLATFORM_ADJUSTMENT | COMPANY_WALLET | COMPANY_RECEIVABLE | STATION_PAYABLE"
        uuid company_id FK
        uuid station_id FK
    }
    ledger_transactions {
        uuid id PK
        text source_type "TOPUP | FUELING | PAYOUT | REVERSAL | ADJUSTMENT"
        uuid source_id "the top-up, fueling, payout or reversal"
        uuid company_id FK
        uuid reverses_transaction_id FK "a reversal names what it mirrors"
        text reference
        text description
        timestamptz posted_at
        uuid posted_by
    }
    ledger_entries {
        uuid id PK
        bigint seq "global order"
        uuid transaction_id FK
        uuid account_id FK
        uuid company_id FK
        text direction "DEBIT | CREDIT"
        bigint amount_piastres "> 0"
        bigint balance_after_piastres "for wallets and payables"
        timestamptz posted_at
    }
    company_wallets {
        uuid company_id PK, FK
        uuid account_id FK
        bigint balance_piastres ">= 0, cached"
    }
    station_payables {
        uuid station_id PK, FK
        uuid operator_id FK
        uuid account_id FK
        bigint balance_piastres "cached; may go below 0"
    }
    topup_requests {
        uuid id PK
        uuid company_id FK
        text origin "COMPANY_REQUEST | OPS_RECORDED (V12)"
        bigint amount_piastres "what was claimed; never credited"
        text receiving_account "which ZAWED account (V12)"
        text bank_reference
        text bank_reference_key "unique per receiving account among live rows"
        date transfer_date
        text status "PENDING | PENDING_APPROVAL | CONFIRMED | REJECTED | CANCELLED"
        uuid requested_by
        bigint verified_amount_piastres "what arrived: the amount credited"
        uuid verified_by "the maker"
        text evidence_reference
        uuid decided_by "the checker, never the maker"
        boolean legacy_single_step "confirmed before V12"
        text decision_reason
        uuid ledger_transaction_id FK
    }
    wallet_adjustments {
        uuid id PK
        uuid company_id FK
        bigint amount_piastres "a credit"
        text reason
        text status "PENDING_APPROVAL | APPROVED | REJECTED | CANCELLED"
        uuid requested_by
        uuid decided_by "never the requester"
        uuid ledger_transaction_id FK
    }
    reconciliation_runs {
        uuid id PK
        text trigger "SCHEDULED | MANUAL"
        timestamptz started_at
        timestamptz finished_at
        int wallets_checked
        int payables_checked
        int findings_count
    }
    reconciliation_findings {
        uuid id PK
        uuid run_id FK
        uuid company_id FK
        uuid station_id FK
        text kind "BALANCE_MISMATCH | MISSING_WALLET | PAYABLE_MISMATCH | MISSING_PAYABLE"
        bigint cached_balance_piastres
        bigint ledger_balance_piastres
        bigint difference_piastres
    }
```

- **Double entry, immutable.** A transaction has two or more entries whose debits equal their credits (checked by a deferred trigger). Entries and transactions are never updated or deleted. A mistake is corrected with a **reversal**: a new transaction that names the original in `reverses_transaction_id`, with at most one reversal per transaction.
- **Cached balances.** `company_wallets.balance_piastres` and `station_payables.balance_piastres` change only under their row lock, in the same transaction as the entry, and the entry records `balance_after_piastres`. A wallet can never go below zero. A station payable can, when a fueling is reversed after the station was already paid.
- **Receivable:** when an offline fueling arrives and the wallet cannot cover it, the shortfall is debited to `company:{id}:receivable`. That is money the company owes; who settles it is an open owner decision.
- **Maker-checker (V12, ADR 0021).** A top-up is claimed (`PENDING`), verified by Operations with the amount that actually arrived (`PENDING_APPROVAL`), and approved by a different user holding `TOPUP_APPROVE` (`CONFIRMED`), which posts the verified amount, never the claimed one. Wallet adjustments (credits that are not bank transfers) follow the same rule and post against `platform:adjustments`. The two-person rule and "the verifier and decider are the acting user" are CHECK constraints and a guard trigger, not only service code.
- **Reconciliation** runs nightly (and on demand by Super Admin). It recomputes every wallet and payable from its entries and records any difference as a finding.

---

## 9. Fuelings, alerts and offline sync (`V7`, `V7_1`, `V8`, `V8_1`; module `fueling`, `sync`)

```mermaid
erDiagram
    companies ||--o{ fuelings : ""
    vehicles ||--o{ fuelings : ""
    rfid_tags ||--o{ fuelings : "read at the pump"
    station_operators ||--o{ fuelings : ""
    stations ||--o{ fuelings : ""
    pos_devices ||--o{ fuelings : ""
    agents ||--o{ fuelings : "attendant"
    fuelings |o--o| ledger_transactions : "when charged"
    fuelings |o--o{ alerts : raises
    pos_devices ||--o{ sync_batches : sends

    fuelings {
        uuid id PK
        uuid client_txn_id UK "made by the device; idempotency key"
        text reference UK "F-10001, on the receipt"
        text status "COMPLETED | DECLINED"
        text decline_reason "COOLDOWN ... INSUFFICIENT_BALANCE"
        text origin "ONLINE | OFFLINE"
        text_array conflicts "offline only: what the snapshot got wrong"
        boolean payable_held "offline only: excluded from payouts"
        uuid company_id FK
        uuid vehicle_id FK
        uuid tag_id FK
        uuid operator_id FK
        uuid station_id FK
        uuid device_id FK
        uuid agent_id FK
        text fuel_type
        bigint volume_ml
        bigint unit_price_piastres
        bigint amount_piastres
        boolean billable "false only for VIP while the VIP flow is on"
        timestamptz evaluated_at "server time, or deviceAt offline"
        timestamptz device_at
        timestamptz received_at
        text receipt_locale "ar | en"
        text plate "as at the pump"
        text company_name_ar
        text company_name_en
        text station_name_ar
        text station_name_en
        text agent_worker_id
        uuid ledger_transaction_id FK
    }
    alerts {
        uuid id PK
        text type "TANK_CAPACITY_EXCEEDED | VIP_CEILING_PASSED | LOW_BALANCE | SYNC_CONFLICT | SYNC_REJECTED | OFFLINE_EXPOSURE | OUTCOME_MISMATCH | COMPANY_OFFLINE_EXPOSURE"
        text severity "WARNING | CRITICAL"
        text status "OPEN | ACKNOWLEDGED"
        uuid company_id FK
        uuid operator_id FK
        uuid station_id FK
        uuid vehicle_id FK
        uuid fueling_id FK
        uuid device_id FK
        jsonb details
        boolean company_visible
        uuid acknowledged_by
        timestamptz acknowledged_at
    }
    sync_batches {
        uuid id PK
        uuid device_batch_id "the device's own id for the batch"
        uuid device_id FK
        uuid operator_id FK
        uuid station_id FK
        timestamptz received_at
        int item_count "max 100"
        int recorded
        int duplicates
        int rejected
        int retried
        int with_conflicts
        int pending_reported "still queued on the device"
    }
```

- **`fuelings` is the heart of the system and is immutable**: a trigger refuses `UPDATE`, `DELETE` and `TRUNCATE` for every role, including the owner (`V7_1`). Another trigger checks that the company, vehicle and tag agree with each other, and that the operator, station, device and agent agree with each other.
- **Declined fuelings are recorded too**, with `decline_reason`. Only a completed, billable fueling with an amount above zero has a ledger transaction (a CHECK).
- **Names are copied** (plate, company, station, agent worker ID) so each audience reads a fueling without seeing the other side's master data, and a later rename never rewrites a receipt.
- **Offline fuelings** (`origin = OFFLINE`) are always `COMPLETED`. Whatever the device's snapshot got wrong goes into `conflicts`, and the server raises a `SYNC_CONFLICT` alert. `payable_held` marks a payable kept back for Operations (`OVER_OFFLINE_ALLOWANCE`, `STATION_NOT_ACTIVE`). See `b5-sync-contract.md`.
- **Alerts** never block a fueling. `company_visible = false` hides an alert from the company (the VIP ceiling).

---

## 10. Station finance (`V9`; module `stationfinance`)

```mermaid
erDiagram
    stations ||--o{ station_bank_accounts : "account history"
    station_bank_accounts ||--o{ payouts : "paid to"
    stations ||--o{ payouts : ""
    payouts |o--o| ledger_transactions : "when posted"
    fuelings ||--o{ payable_hold_releases : "release of a held payable"
    fuelings ||--o| fueling_reversals : "at most one approved"
    fueling_reversals |o--o| ledger_transactions : "the mirror posting"
    stations ||--|| station_payables : "what it is owed"

    station_bank_accounts {
        uuid id PK
        uuid station_id FK
        uuid operator_id FK
        text account_kind "IBAN | ACCOUNT_NUMBER"
        bytea account_encrypted "never returned by the API"
        text account_last4
        text bank_name
        text holder_name
        text bank_letter_reference "the bank's confirmation letter"
        text status "PENDING | ACTIVE | REJECTED | SUPERSEDED"
        uuid requested_by
        uuid decided_by "never = requested_by"
        timestamptz payouts_held_until "approval + 72 h by default"
        timestamptz superseded_at
    }
    payouts {
        uuid id PK
        uuid station_id FK
        uuid operator_id FK
        uuid bank_account_id FK
        bigint amount_piastres
        text bank_reference
        text bank_reference_key UK "one transfer pays once"
        date value_date
        text status "PENDING | POSTED | CANCELLED"
        bigint approval_threshold_piastres "in force when recorded"
        boolean approval_required "amount > threshold"
        uuid requested_by
        uuid decided_by "the second person"
        timestamptz posted_at
        uuid ledger_transaction_id FK
    }
    payable_hold_releases {
        uuid id PK
        uuid fueling_id FK
        uuid station_id FK
        uuid operator_id FK
        bigint amount_piastres
        text reason
        uuid released_by
        timestamptz released_at
    }
    fueling_reversals {
        uuid id PK
        uuid fueling_id FK
        uuid company_id FK
        uuid operator_id FK
        uuid station_id FK
        bigint amount_piastres
        text reason
        text status "PENDING | APPROVED | REJECTED"
        uuid requested_by
        uuid decided_by "never = requested_by"
        uuid ledger_transaction_id FK
    }
```

- **Position of a station:** *balance* (from `station_payables`), minus *held* (offline payables not yet released or reversed), gives *available*. A new payout may take at most *available − pending payouts*.
- **Dual control is enforced by the database, not only the API.** For each of a bank account change, a payout above the threshold (default 50,000 EGP, `zawed.finance.payout-approval-threshold-piastres`) and a reversal, a CHECK refuses a decision by the person who asked.
- **A new bank account receives nothing during a hold window** (`zawed.finance.bank-change-hold-hours`, default 72 h), so a fraudulent change can be noticed before money follows it.
- **A reversal** posts the exact mirror of the fueling's transaction: the wallet and receivable are refunded, and the station payable is debited.

---

## 11. Support tickets (`V11`; module `tickets`)

```mermaid
erDiagram
    companies |o--o{ tickets : opens
    station_operators |o--o{ tickets : opens
    tickets ||--|{ ticket_messages : thread
    vehicles |o--o{ tickets : "about (optional)"
    stations |o--o{ tickets : "about (optional)"
    fuelings |o--o{ tickets : "about (optional)"

    tickets {
        uuid id PK
        text reference UK "T-1001"
        text scope_type "COMPANY | OPERATOR"
        uuid company_id FK
        uuid operator_id FK
        text type "LOST_TAG | STOLEN_TAG | LIMIT_CHANGE | VEHICLE_CHANGE | TOPUP_QUERY | DEVICE_FAULT | TAG_READ_ISSUE | PAYOUT_QUERY | PRICE_QUERY | DISPUTE | OTHER"
        text subject
        text status "OPEN | IN_PROGRESS | RESOLVED | CLOSED"
        uuid vehicle_id FK
        uuid station_id FK
        uuid fueling_id FK
        uuid created_by
        uuid assigned_to "Ops user"
        timestamptz resolved_at
    }
    ticket_messages {
        uuid id PK
        uuid ticket_id FK
        uuid author_id
        text author_role
        text body
        boolean internal "Ops-only note"
        timestamptz created_at
    }
```

A ticket is a **request, not a change**: the company and station portals stay read-only for master data, and Operations makes the change. Each side sees only its own tickets. Internal notes are visible to Operations only (row-level security). Messages are append-only, and only Operations moves a ticket's status.

---

## 11b. Bulk vehicle import (`V15`; module `fleet`)

```mermaid
erDiagram
    companies ||--o{ fleet_import_jobs : "one file each"
    fleet_import_jobs ||--|{ fleet_import_rows : rows
    fleet_import_rows |o--o| vehicles : "created"

    fleet_import_jobs {
        uuid id PK
        uuid company_id FK
        text file_name
        text file_sha256 "same file again is flagged"
        text file_format "CSV | XLSX"
        text status "VALIDATED | QUEUED | RUNNING | COMPLETED | COMPLETED_WITH_ERRORS | CANCELLED"
        text mode "STRICT | VALID_ROWS_ONLY"
        int row_count "at most 5,000"
        int valid_count
        int invalid_count
        int imported_count
        int existing_count
        int failed_count
        uuid confirmed_by "the worker acts for this user"
        text lease_owner "the worker holding it"
        timestamptz lease_until "expired lease: another worker resumes"
    }
    fleet_import_rows {
        uuid job_id PK, FK
        int row_number PK "as in the file"
        text plate
        text plate_key
        text fleet_code
        jsonb data "normalised values; the file is not kept"
        text status "VALID | INVALID | DUPLICATE_IN_FILE | EXISTS | IMPORTED | FAILED | SKIPPED"
        jsonb errors "column and code per problem"
        uuid vehicle_id FK
    }
```

- **The upload creates nothing.** Every row is validated and stored first. `STRICT` (the default) confirms only a file without problems; `VALID_ROWS_ONLY` must be chosen explicitly.
- **One active import per company** (partial unique index on `QUEUED`/`RUNNING`).
- **The worker** (ADR 0026) claims a job with `FOR UPDATE SKIP LOCKED` and a lease. Each row's vehicle and outcome commit together, so a crash resumes at the first `VALID` row and nothing is created twice. The plate unique index is the last defence; a plate registered meanwhile makes the row `EXISTS`.
- **Platform only (RLS):** imports are an Operations action, never visible to the company.

---

## 12. Who can see what (row-level security)

Every request sets three session values (`app.scope_type`, `app.scope_id`, `app.user_id`), read by `app_scope_type()`, `app_scope_id()` and `app_is_platform()`. The policies below filter every query, so a bug in a service cannot leak another tenant's rows. The API also checks the scope, so a forbidden row answers 404.

| Table | Platform (Super Admin, Ops) | Company (Fleet Manager, Accountant) | Operator (Station Manager) |
|---|---|---|---|
| users | all | users of the same company | users of the same operator |
| companies, departments, vehicles, rfid_tags, limit_rules | all, write | own, read | — |
| station_operators, stations, pos_devices, agents | all, write | — | own, read |
| agent_sessions | all | — | own stations (V10) |
| fuel_prices | all, write | all, read | all, read |
| company_wallets, topup_requests, wallet entries | all | own (may withdraw only a `PENDING` request, V12) | — |
| station_payables, payable entries | all | — | own stations |
| fuelings | all | own (company_id) | own stations (operator_id) |
| sync_batches | all | — | own devices |
| alerts | all | own where `company_visible` | — |
| station_bank_accounts | all | — | own, `ACTIVE` and `PENDING` only |
| payouts | all | — | own, `POSTED` only |
| fueling_reversals | all | own, `APPROVED` only | own stations, `APPROVED` only |
| wallet_adjustments, fleet_import_jobs, fleet_import_rows | all | — | — |
| idempotency_keys | own keys only (every scope) | own keys only | own keys only |
| tickets / ticket_messages | all, with internal notes | own, without internal notes | own, without internal notes |
| audit_log | all (the API endpoint is Super Admin only) | rows of its own scope (no endpoint yet) | rows of its own scope (no endpoint yet) |

POS devices have no user. The device channel, fueling engine, offline sync and snapshot run explicitly in the platform scope (`DbScope.runAs`) after the device proof is checked. `ArchitectureTests` keeps the list of places allowed to do that closed.

---

## 13. How money moves (ledger postings)

| Event | Debit | Credit | Where |
|---|---|---|---|
| Top-up approved (the verified amount; V12) | `platform:bank` | `company:{id}:wallet` | `ledger.TopUps` |
| Wallet adjustment approved (V12) | `platform:adjustments` | `company:{id}:wallet` | `ledger.WalletAdjustments` |
| Fueling completed (online, or offline with enough balance) | `company:{id}:wallet` | `station:{id}:payable` | `fueling.FuelingEngine` |
| Offline fueling beyond the wallet | wallet (what it holds) + `company:{id}:receivable` (the rest) | `station:{id}:payable` (full amount) | `fueling.FuelingEngine` |
| Payout posted | `station:{id}:payable` | `platform:bank` | `stationfinance.Payouts` |
| Reversal approved | the mirror of the fueling: `station:{id}:payable` | wallet / receivable | `stationfinance.Reversals` |

Every posting goes through `ledger.Ledger`. It is the only writer of entries, takes locks in a fixed order (wallet → vehicle → tag for fuelings; payable after any wallet), and is checked by the nightly reconciliation.

---

## 14. Migration history

| Version | Phase | What it adds |
|---|---|---|
| V1 | B0 | Baseline and conventions |
| V2 | B1 | users, sessions, mfa_challenges, audit_log |
| V3 | B1 | Runtime role `zawed_app`, scope functions, RLS on identity, sign-in functions |
| V4 | B2 | Companies, fleet, operators, stations, devices, agents, prices, RLS, same-tenant triggers |
| V5 | B2 | RLS bound to the runtime role |
| V6, V6_1 | B3 | Ledger, wallets, top-ups, reconciliation; hardening |
| V7, V7_1 | B4 | Device keys and nonces, agent sessions, fuelings, alerts; fuelings immutable |
| V8, V8_1 | B5 | Offline: receivable account, conflicts, sync batches, held payables |
| V9 | B6 | Station payables, reversal links, bank accounts, payouts, hold releases, reversals; MFA for station managers |
| V10 | B6 | Station managers read their shifts |
| V11 | B6 | Support tickets |
| V12 | Hardening | Maker-checker top-ups (verified amount, receiving account, evidence), `OPS_MANAGER` role, wallet adjustments with `platform:adjustments`, guard triggers |
| V13 | Hardening | `idempotency_keys` |
| V14 | Hardening | Immutable tag and vehicle identity, tank ceiling, `fleet_code`, canonical plate keys, nonce purge function, company offline index and alert type |
| V15 | Hardening | Bulk vehicle import: `fleet_import_jobs`, `fleet_import_rows` |
| V16 | Hardening | Fueling time indexes: `(company_id, evaluated_at)`, `(operator_id, evaluated_at)`, BRIN `(evaluated_at)`, measured on 5 M rows |

To see the live schema, run the API locally (`apps/api/README.md`) and connect to the embedded database, or read the migrations in order.
