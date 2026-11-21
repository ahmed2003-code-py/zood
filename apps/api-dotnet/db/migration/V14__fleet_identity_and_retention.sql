-- Production hardening (audit H1, H5, H6, F5, F7; ADR 0023, 0024):
--
--  1. Identity columns never change: a tag row is one assignment of one UID to one vehicle of one
--     company, for ever; a vehicle never moves to another company. This makes rfid_tags itself the
--     assignment history (no separate assignment table is needed).
--  2. A vehicle's tank is at most 1,500 L, the ceiling of a single fueling (typos are refused).
--  3. fleet_code: the company's own number for a vehicle, optional, unique per company among
--     vehicles in service. Used by fleet managers and to match bulk import rows.
--  4. Plate keys are canonical for the Egyptian format (one digit group, one letter group): the
--     digits come first whatever order they were typed in, so "١٢٣٤ ط ب ع" and "ط ب ع 1234" are one
--     plate. Keys of any other shape are left exactly as they were (never a false collision).
--  5. Device nonces are purged after an hour (replay window: 10 minutes) by a bounded function.
--  6. Company-wide offline monitoring: an index for a company's recent offline fuelings and an alert
--     type for crossing the monitoring threshold (it never blocks a fueling).

-- ----------------------------------------------------------------------------------------------
-- 1. Immutable identity columns
-- ----------------------------------------------------------------------------------------------

CREATE FUNCTION fleet_identity_guard() RETURNS trigger
  LANGUAGE plpgsql SET search_path = public, pg_temp
  AS $$
  BEGIN
    IF TG_TABLE_NAME = 'rfid_tags' THEN
      IF NEW.company_id IS DISTINCT FROM OLD.company_id OR NEW.vehicle_id IS DISTINCT FROM OLD.vehicle_id
         OR NEW.uid_encrypted IS DISTINCT FROM OLD.uid_encrypted OR NEW.uid_lookup IS DISTINCT FROM OLD.uid_lookup
         OR NEW.uid_last4 IS DISTINCT FROM OLD.uid_last4 THEN
        RAISE EXCEPTION 'tag %: its UID, vehicle and company never change; issue a new tag instead', OLD.id
          USING ERRCODE = 'check_violation';
      END IF;
    ELSIF NEW.company_id IS DISTINCT FROM OLD.company_id THEN
      RAISE EXCEPTION 'vehicle %: it never moves to another company; decommission it and register it there', OLD.id
        USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;
REVOKE ALL ON FUNCTION fleet_identity_guard() FROM PUBLIC;
CREATE TRIGGER rfid_tags_identity_guard BEFORE UPDATE ON rfid_tags
  FOR EACH ROW EXECUTE FUNCTION fleet_identity_guard();
CREATE TRIGGER vehicles_identity_guard BEFORE UPDATE ON vehicles
  FOR EACH ROW EXECUTE FUNCTION fleet_identity_guard();

-- ----------------------------------------------------------------------------------------------
-- 2. Tank ceiling (validated separately so a large table is scanned without blocking writes)
-- ----------------------------------------------------------------------------------------------

ALTER TABLE vehicles ADD CONSTRAINT vehicles_tank_capacity_ceiling CHECK (tank_capacity_ml <= 1500000) NOT VALID;
ALTER TABLE vehicles VALIDATE CONSTRAINT vehicles_tank_capacity_ceiling;

-- ----------------------------------------------------------------------------------------------
-- 3. Fleet code
-- ----------------------------------------------------------------------------------------------

ALTER TABLE vehicles ADD COLUMN fleet_code text CHECK (fleet_code ~ '^[^[:cntrl:]]{1,40}$');
CREATE UNIQUE INDEX vehicles_fleet_code_key ON vehicles (company_id, upper(fleet_code))
  WHERE fleet_code IS NOT NULL AND status <> 'DECOMMISSIONED';
COMMENT ON COLUMN vehicles.fleet_code IS
  'The company''s own number for the vehicle (optional). Unique per company among vehicles in service.';

-- ----------------------------------------------------------------------------------------------
-- 4. Canonical plate keys (Egyptian shape only), refusing to merge two vehicles
-- ----------------------------------------------------------------------------------------------

CREATE FUNCTION plate_key_canonical(p_key text) RETURNS text
  LANGUAGE sql IMMUTABLE PARALLEL SAFE
  AS $$
    SELECT CASE WHEN p_key ~ '^[0-9]+[^0-9]+$' OR p_key ~ '^[^0-9]+[0-9]+$'
                THEN regexp_replace(p_key, '[^0-9]', '', 'g') || regexp_replace(p_key, '[0-9]', '', 'g')
                ELSE p_key END
  $$;

DO $$
DECLARE
  clash record;
BEGIN
  SELECT plate_key_canonical(plate_key) AS key, array_agg(id) AS ids INTO clash
    FROM vehicles WHERE status <> 'DECOMMISSIONED'
    GROUP BY plate_key_canonical(plate_key) HAVING count(*) > 1 LIMIT 1;
  IF FOUND THEN
    RAISE EXCEPTION 'V14: vehicles % would share the plate key %; decommission the duplicate first', clash.ids, clash.key
      USING ERRCODE = 'unique_violation';
  END IF;
END
$$;

UPDATE vehicles SET plate_key = plate_key_canonical(plate_key) WHERE plate_key <> plate_key_canonical(plate_key);

-- ----------------------------------------------------------------------------------------------
-- 5. Nonce retention
-- ----------------------------------------------------------------------------------------------

-- Deletes at most p_limit nonces older than p_before and says how many. A token request is accepted
-- only within ±5 minutes of its own timestamp, so a nonce can be replayed for at most 10 minutes; the
-- caller passes an hour ago. SECURITY DEFINER: the job has no user and no scope, and device_nonces
-- is otherwise read and written only by the device channel.
CREATE FUNCTION purge_device_nonces(p_before timestamptz, p_limit integer) RETURNS integer
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
  AS $$
  DECLARE
    deleted integer;
  BEGIN
    IF p_before > now() - interval '15 minutes' THEN
      RAISE EXCEPTION 'purge_device_nonces: % is inside the replay window', p_before USING ERRCODE = 'check_violation';
    END IF;
    DELETE FROM device_nonces d
     USING (SELECT device_id, nonce FROM device_nonces WHERE created_at < p_before
             ORDER BY created_at LIMIT least(greatest(p_limit, 1), 50000)) old
     WHERE d.device_id = old.device_id AND d.nonce = old.nonce;
    GET DIAGNOSTICS deleted = ROW_COUNT;
    RETURN deleted;
  END
  $$;
REVOKE ALL ON FUNCTION purge_device_nonces(timestamptz, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION purge_device_nonces(timestamptz, integer) TO zawed_app;

-- ----------------------------------------------------------------------------------------------
-- 6. Company-wide offline monitoring
-- ----------------------------------------------------------------------------------------------

CREATE INDEX fuelings_company_offline_idx ON fuelings (company_id, evaluated_at) WHERE origin = 'OFFLINE';

ALTER TABLE alerts DROP CONSTRAINT alerts_type_check;
ALTER TABLE alerts ADD CONSTRAINT alerts_type_check CHECK (type IN (
  'TANK_CAPACITY_EXCEEDED', 'VIP_CEILING_PASSED', 'LOW_BALANCE', 'SYNC_CONFLICT', 'SYNC_REJECTED', 'OFFLINE_EXPOSURE',
  'OUTCOME_MISMATCH', 'COMPANY_OFFLINE_EXPOSURE'));
