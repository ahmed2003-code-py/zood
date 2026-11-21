-- B4 review findings 12 and 3 (docs/architecture/b4-fueling-contract.md, ADR 0018).
--
-- 1. A fueling row is immutable for every role, the owner included, like a ledger entry (V6):
--    the missing grants already stop the runtime role; this stops everyone else.
-- 2. A fueling's tenants agree with its references, because row-level security reads
--    company_id and operator_id: the vehicle and tag belong to the company, the station to the
--    operator, the device and agent to the station.

CREATE FUNCTION fuelings_refuse_change() RETURNS trigger
  LANGUAGE plpgsql
  AS $$
  BEGIN
    RAISE EXCEPTION '% is immutable: % is not allowed', TG_TABLE_NAME, TG_OP
      USING ERRCODE = 'insufficient_privilege';
  END
  $$;

CREATE TRIGGER fuelings_immutable BEFORE UPDATE OR DELETE ON fuelings
  FOR EACH ROW EXECUTE FUNCTION fuelings_refuse_change();
CREATE TRIGGER fuelings_no_truncate BEFORE TRUNCATE ON fuelings
  FOR EACH STATEMENT EXECUTE FUNCTION fuelings_refuse_change();

CREATE FUNCTION fuelings_same_tenant() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
  AS $$
  BEGIN
    IF NOT EXISTS (SELECT 1 FROM vehicles WHERE id = NEW.vehicle_id AND company_id = NEW.company_id)
       OR NOT EXISTS (SELECT 1 FROM rfid_tags WHERE id = NEW.tag_id AND vehicle_id = NEW.vehicle_id
                                                AND company_id = NEW.company_id) THEN
      RAISE EXCEPTION 'fueling %: vehicle and tag must belong to company %', NEW.id, NEW.company_id
        USING ERRCODE = 'check_violation';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM stations WHERE id = NEW.station_id AND operator_id = NEW.operator_id)
       OR NOT EXISTS (SELECT 1 FROM pos_devices WHERE id = NEW.device_id AND station_id = NEW.station_id)
       OR NOT EXISTS (SELECT 1 FROM agents WHERE id = NEW.agent_id AND station_id = NEW.station_id) THEN
      RAISE EXCEPTION 'fueling %: station, device and agent must belong to operator %', NEW.id, NEW.operator_id
        USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;

REVOKE ALL ON FUNCTION fuelings_same_tenant() FROM PUBLIC;

CREATE TRIGGER fuelings_same_tenant BEFORE INSERT ON fuelings
  FOR EACH ROW EXECUTE FUNCTION fuelings_same_tenant();
