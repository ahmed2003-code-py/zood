-- B5: offline capture and sync (docs/architecture/b5-sync-contract.md, ADR 0019).
--
-- 1. A company receivable account: what a company owes for fuel taken offline while its wallet
--    could not cover it. The wallet never goes below zero (V6 CHECK); the shortfall is a debt on
--    its own account, so the station is paid in full and the ledger still balances.
-- 2. Offline fuelings record the checks the device's snapshot got wrong (conflicts) instead of a
--    decline: an offline fueling has already happened at the pump.
-- 3. Two alert types for sync, and the device an alert concerns.
-- 4. Every batch a device sends, for the sync monitor.

-- ------------------------------------------------------------------------------- 1. receivable

ALTER TABLE ledger_accounts DROP CONSTRAINT ledger_accounts_kind_check;
ALTER TABLE ledger_accounts ADD CONSTRAINT ledger_accounts_kind_check
  CHECK (kind IN ('PLATFORM_BANK', 'COMPANY_WALLET', 'COMPANY_RECEIVABLE', 'STATION_PAYABLE'));

ALTER TABLE ledger_accounts DROP CONSTRAINT ledger_accounts_owner_matches_kind;
ALTER TABLE ledger_accounts ADD CONSTRAINT ledger_accounts_owner_matches_kind CHECK (
     (kind = 'PLATFORM_BANK' AND company_id IS NULL AND station_id IS NULL)
  OR (kind IN ('COMPANY_WALLET', 'COMPANY_RECEIVABLE') AND company_id IS NOT NULL AND station_id IS NULL)
  OR (kind = 'STATION_PAYABLE' AND company_id IS NULL AND station_id IS NOT NULL));

-- V6 allowed a company one ledger account, its wallet (a unique index on company_id alone). It now
-- has two, so the rule becomes one account of each kind.
DROP INDEX ledger_accounts_company_key;
CREATE UNIQUE INDEX ledger_accounts_company_kind_key ON ledger_accounts (company_id, kind)
  WHERE company_id IS NOT NULL;

-- ------------------------------------------------------------------------- 2. offline conflicts

ALTER TABLE fuelings ADD COLUMN conflicts text[] NOT NULL DEFAULT '{}';
ALTER TABLE fuelings ADD CONSTRAINT fuelings_conflicts_known CHECK (conflicts <@ ARRAY[
  'COOLDOWN', 'TAG_INACTIVE', 'VEHICLE_INACTIVE', 'COMPANY_FROZEN', 'FUEL_TYPE_MISMATCH', 'FUEL_NOT_AVAILABLE',
  'LIMIT_EXCEEDED', 'INSUFFICIENT_BALANCE', 'STALE_CAPTURE']);
-- An online fueling is decided, so it declines; only an offline one carries conflicts, and an
-- offline one is never declined (PRD 7.5: it already happened at the pump).
ALTER TABLE fuelings ADD CONSTRAINT fuelings_conflicts_offline_only CHECK (
  (origin = 'ONLINE' AND conflicts = '{}') OR (origin = 'OFFLINE' AND status = 'COMPLETED'));

CREATE INDEX fuelings_offline_conflicts_idx ON fuelings (company_id, id) WHERE conflicts <> '{}';

-- -------------------------------------------------------------------------------- 3. alerts

ALTER TABLE alerts DROP CONSTRAINT alerts_type_check;
ALTER TABLE alerts ADD CONSTRAINT alerts_type_check CHECK (type IN (
  'TANK_CAPACITY_EXCEEDED', 'VIP_CEILING_PASSED', 'LOW_BALANCE', 'SYNC_CONFLICT', 'SYNC_REJECTED'));
ALTER TABLE alerts ADD COLUMN device_id uuid REFERENCES pos_devices (id);

-- A rejected item is reported once. A device may send a batch again (a lost answer, a retry), and
-- an item that was rejected is rejected again: the second time must not raise a second alert.
ALTER TABLE alerts ADD CONSTRAINT alerts_sync_rejected_names_item CHECK (
  type <> 'SYNC_REJECTED' OR (device_id IS NOT NULL AND details ? 'clientTxnId' AND details ? 'code'));
CREATE UNIQUE INDEX alerts_sync_rejected_once ON alerts (device_id, (details ->> 'clientTxnId'), (details ->> 'code'))
  WHERE type = 'SYNC_REJECTED';

-- ---------------------------------------------------------------------------------- 4. batches

CREATE TABLE sync_batches (
  id               uuid PRIMARY KEY,
  -- The id the device gave the batch. Not unique: a batch sent again is recorded again, and
  -- every item in it comes back DUPLICATE.
  device_batch_id  uuid NOT NULL,
  device_id        uuid NOT NULL REFERENCES pos_devices (id),
  operator_id      uuid NOT NULL REFERENCES station_operators (id),
  station_id       uuid NOT NULL REFERENCES stations (id),
  received_at      timestamptz NOT NULL,
  item_count       integer NOT NULL CHECK (item_count BETWEEN 0 AND 100),
  recorded         integer NOT NULL CHECK (recorded >= 0),
  duplicates       integer NOT NULL CHECK (duplicates >= 0),
  rejected         integer NOT NULL CHECK (rejected >= 0),
  with_conflicts   integer NOT NULL CHECK (with_conflicts >= 0),
  -- What the device said it still held after this batch.
  pending_reported integer NOT NULL CHECK (pending_reported >= 0),
  CONSTRAINT sync_batches_counts CHECK (recorded + duplicates + rejected = item_count)
);

CREATE INDEX sync_batches_device_idx ON sync_batches (device_id, id);
CREATE INDEX sync_batches_operator_idx ON sync_batches (operator_id, id);

GRANT SELECT, INSERT ON sync_batches TO zawed_app;                           -- append only

ALTER TABLE sync_batches ENABLE ROW LEVEL SECURITY;
CREATE POLICY sync_batches_by_scope ON sync_batches
  USING (app_is_platform() OR (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id()))
  WITH CHECK (app_is_platform());
