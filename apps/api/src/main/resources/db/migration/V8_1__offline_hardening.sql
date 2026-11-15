-- B5 security review fixes (docs/architecture/b5-sync-contract.md §4, ADR 0019).
--
-- 1. Fuel dispensed for a tag that resolves is always recorded. What used to refuse it is now a
--    conflict on the fueling: the agent's session could not be confirmed, the device clock was
--    ahead, the id had been declined online, the station was not open, the price differed, or the
--    device went past what it may spend offline.
-- 2. A fueling whose payable must wait for Operations (over the offline exposure bound, or at a
--    station that was not open) is marked held; station payouts (B6) exclude held payables until
--    Operations clears them.
-- 3. Two alert types: OFFLINE_EXPOSURE (a device spent more offline than it may) and
--    OUTCOME_MISMATCH (the server completed a fueling the agent was told was refused).
-- 4. A batch item that failed on our side answers RETRY and is counted.

ALTER TABLE fuelings DROP CONSTRAINT fuelings_conflicts_known;
ALTER TABLE fuelings ADD CONSTRAINT fuelings_conflicts_known CHECK (conflicts <@ ARRAY[
  'COOLDOWN', 'TAG_INACTIVE', 'VEHICLE_INACTIVE', 'COMPANY_FROZEN', 'FUEL_TYPE_MISMATCH', 'FUEL_NOT_AVAILABLE',
  'LIMIT_EXCEEDED', 'INSUFFICIENT_BALANCE', 'STALE_CAPTURE', 'AGENT_UNVERIFIED', 'DEVICE_CLOCK_SKEW', 'DECLINED_ONLINE',
  'STATION_NOT_ACTIVE', 'PRICE_MISMATCH', 'OVER_OFFLINE_ALLOWANCE']);

ALTER TABLE fuelings ADD COLUMN payable_held boolean NOT NULL DEFAULT false;
ALTER TABLE fuelings ADD CONSTRAINT fuelings_held_offline_only CHECK (NOT payable_held OR origin = 'OFFLINE');
CREATE INDEX fuelings_payable_held_idx ON fuelings (station_id, id) WHERE payable_held;
-- The offline exposure bound sums one device's recent offline fuelings for one company.
CREATE INDEX fuelings_offline_exposure_idx ON fuelings (device_id, company_id, evaluated_at) WHERE origin = 'OFFLINE';

ALTER TABLE alerts DROP CONSTRAINT alerts_type_check;
ALTER TABLE alerts ADD CONSTRAINT alerts_type_check CHECK (type IN (
  'TANK_CAPACITY_EXCEEDED', 'VIP_CEILING_PASSED', 'LOW_BALANCE', 'SYNC_CONFLICT', 'SYNC_REJECTED', 'OFFLINE_EXPOSURE',
  'OUTCOME_MISMATCH'));
-- One outcome-mismatch alert per fueling, however often the terminal asks.
CREATE UNIQUE INDEX alerts_outcome_mismatch_once ON alerts (fueling_id) WHERE type = 'OUTCOME_MISMATCH';

ALTER TABLE sync_batches ADD COLUMN retried integer NOT NULL DEFAULT 0 CHECK (retried >= 0);
ALTER TABLE sync_batches DROP CONSTRAINT sync_batches_counts;
ALTER TABLE sync_batches ADD CONSTRAINT sync_batches_counts CHECK (recorded + duplicates + rejected + retried = item_count);
