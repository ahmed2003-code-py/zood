-- Master data (phase B2): companies and departments, vehicles, RFID tags and limit rules, station
-- operators and stations, POS devices and agents, fuel prices.
--
-- Rules from V1 apply. In addition:
--   * Every scoped table carries the id of its tenant (company_id or operator_id), even when it could
--     be reached through a parent, so its row-level security policy needs no join.
--   * The runtime role zawed_app gets SELECT, INSERT, UPDATE and nothing else: rows are retired by
--     status, never deleted. Fuel prices are insert-only: a price is never edited, a new one is
--     scheduled.

-- ------------------------------------------------------------------------------------- companies

CREATE TABLE companies (
  id                              uuid PRIMARY KEY,
  legal_name                      text NOT NULL,
  name_ar                         text NOT NULL,
  name_en                         text NOT NULL,
  tax_id                          text,
  commercial_registration         text,
  contact_name                    text,
  contact_phone                   text,
  contact_email                   text,
  status                          text NOT NULL CHECK (status IN ('ACTIVE', 'FROZEN')),
  -- Minutes between two fuelings of the same tag (PRD 6.4: 2 to 4 hours, per company).
  cooldown_minutes                integer NOT NULL CHECK (cooldown_minutes BETWEEN 120 AND 240),
  low_balance_threshold_piastres  bigint NOT NULL CHECK (low_balance_threshold_piastres >= 0),
  created_at                      timestamptz NOT NULL,
  created_by                      uuid,
  updated_at                      timestamptz NOT NULL,
  updated_by                      uuid
);

CREATE UNIQUE INDEX companies_tax_id_key ON companies (tax_id) WHERE tax_id IS NOT NULL;
CREATE INDEX companies_status_id_idx ON companies (status, id);

CREATE TABLE departments (
  id                        uuid PRIMARY KEY,
  company_id                uuid NOT NULL REFERENCES companies (id),
  name                      text NOT NULL,
  -- Monitoring only: a budget never blocks a fueling (approved decision; limits block).
  monthly_budget_piastres   bigint CHECK (monthly_budget_piastres >= 0),
  status                    text NOT NULL CHECK (status IN ('ACTIVE', 'ARCHIVED')),
  created_at                timestamptz NOT NULL,
  created_by                uuid,
  updated_at                timestamptz NOT NULL,
  updated_by                uuid
);

CREATE INDEX departments_company_id_idx ON departments (company_id, id);
CREATE UNIQUE INDEX departments_name_key ON departments (company_id, lower(name)) WHERE status = 'ACTIVE';

-- ---------------------------------------------------------------------------------------- fleet

CREATE TABLE vehicles (
  id                       uuid PRIMARY KEY,
  company_id               uuid NOT NULL REFERENCES companies (id),
  department_id            uuid REFERENCES departments (id),
  -- Shown exactly as registered, never transliterated; plate_key only finds duplicates.
  plate                    text NOT NULL,
  plate_key                text NOT NULL,
  make                     text NOT NULL,
  model                    text NOT NULL,
  year                     integer CHECK (year BETWEEN 1950 AND 2100),
  fuel_type                text NOT NULL CHECK (fuel_type IN ('GAS_80', 'GAS_92', 'GAS_95', 'DIESEL', 'CNG')),
  tank_capacity_ml         bigint NOT NULL CHECK (tank_capacity_ml > 0),
  tier                     text NOT NULL CHECK (tier IN ('NORMAL', 'VIP')),
  -- Invisible VIP safeguard (PRD 4.5): flags, never blocks. Only for VIP vehicles.
  vip_monthly_ceiling_ml   bigint CHECK (vip_monthly_ceiling_ml > 0),
  status                   text NOT NULL CHECK (status IN ('ACTIVE', 'SUSPENDED', 'DECOMMISSIONED')),
  created_at               timestamptz NOT NULL,
  created_by               uuid,
  updated_at               timestamptz NOT NULL,
  updated_by               uuid,
  CONSTRAINT vehicles_vip_ceiling_only_for_vip CHECK (tier = 'VIP' OR vip_monthly_ceiling_ml IS NULL)
);

CREATE INDEX vehicles_company_id_idx ON vehicles (company_id, id);
CREATE INDEX vehicles_department_id_idx ON vehicles (department_id);
-- A plate belongs to one live vehicle; a decommissioned one frees it.
CREATE UNIQUE INDEX vehicles_plate_key ON vehicles (plate_key) WHERE status <> 'DECOMMISSIONED';

CREATE TABLE rfid_tags (
  id                  uuid PRIMARY KEY,
  company_id          uuid NOT NULL REFERENCES companies (id),
  vehicle_id          uuid NOT NULL REFERENCES vehicles (id),
  -- The tag UID is never stored in clear: encrypted for display to Ops, plus a keyed hash to find it.
  uid_encrypted       bytea NOT NULL,
  uid_lookup          text NOT NULL,
  uid_last4           text NOT NULL,
  status              text NOT NULL
    CHECK (status IN ('ACTIVE', 'LOST', 'STOLEN', 'DEACTIVATED', 'REPLACEMENT_PENDING')),
  status_reason       text,
  -- Mirror of the cooldown record written on the tag itself (PRD 7.3).
  cooldown_until      timestamptz,
  last_write_status   text CHECK (last_write_status IN ('OK', 'FAILED')),
  created_at          timestamptz NOT NULL,
  created_by          uuid,
  updated_at          timestamptz NOT NULL,
  updated_by          uuid
);

CREATE INDEX rfid_tags_company_id_idx ON rfid_tags (company_id);
CREATE INDEX rfid_tags_vehicle_id_idx ON rfid_tags (vehicle_id, id);
-- A physical tag is bound once, ever: a lost tag must not come back on another vehicle.
CREATE UNIQUE INDEX rfid_tags_uid_lookup_key ON rfid_tags (uid_lookup);
-- At most one usable tag per vehicle.
CREATE UNIQUE INDEX rfid_tags_one_active_per_vehicle ON rfid_tags (vehicle_id) WHERE status = 'ACTIVE';

CREATE TABLE limit_rules (
  id             uuid PRIMARY KEY,
  company_id     uuid NOT NULL REFERENCES companies (id),
  vehicle_id     uuid NOT NULL REFERENCES vehicles (id),
  kind           text NOT NULL CHECK (kind IN ('VOLUME', 'AMOUNT')),
  period         text NOT NULL CHECK (period IN ('DAY', 'WEEK', 'MONTH')),
  -- Millilitres for VOLUME, piastres for AMOUNT.
  max_value      bigint NOT NULL CHECK (max_value > 0),
  active         boolean NOT NULL,
  created_at     timestamptz NOT NULL,
  created_by     uuid,
  deactivated_at timestamptz,
  deactivated_by uuid
);

CREATE INDEX limit_rules_company_id_idx ON limit_rules (company_id);
CREATE INDEX limit_rules_vehicle_id_idx ON limit_rules (vehicle_id);
-- One active rule per vehicle, kind and period; a change deactivates the old one and adds a new one.
CREATE UNIQUE INDEX limit_rules_one_active ON limit_rules (vehicle_id, kind, period) WHERE active;

-- -------------------------------------------------------------------------------------- network

CREATE TABLE station_operators (
  id                        uuid PRIMARY KEY,
  legal_name                text NOT NULL,
  name_ar                   text NOT NULL,
  name_en                   text NOT NULL,
  tax_id                    text,
  commercial_registration   text,
  contact_name              text,
  contact_phone             text,
  contact_email             text,
  status                    text NOT NULL CHECK (status IN ('ACTIVE', 'SUSPENDED')),
  created_at                timestamptz NOT NULL,
  created_by                uuid,
  updated_at                timestamptz NOT NULL,
  updated_by                uuid
);

CREATE UNIQUE INDEX station_operators_tax_id_key ON station_operators (tax_id) WHERE tax_id IS NOT NULL;

CREATE TABLE stations (
  id             uuid PRIMARY KEY,
  operator_id    uuid NOT NULL REFERENCES station_operators (id),
  name_ar        text NOT NULL,
  name_en        text NOT NULL,
  address        text,
  latitude       numeric(9, 6) CHECK (latitude BETWEEN -90 AND 90),
  longitude      numeric(9, 6) CHECK (longitude BETWEEN -180 AND 180),
  -- Rules such as daily limits are evaluated in the station's own time zone (PRD 2.3).
  time_zone      text NOT NULL,
  connectivity   text NOT NULL CHECK (connectivity IN ('NORMAL', 'LOW')),
  fuel_types     text[] NOT NULL CHECK (fuel_types <@ ARRAY['GAS_80', 'GAS_92', 'GAS_95', 'DIESEL', 'CNG']),
  status         text NOT NULL CHECK (status IN ('ACTIVE', 'SUSPENDED', 'CLOSED')),
  created_at     timestamptz NOT NULL,
  created_by     uuid,
  updated_at     timestamptz NOT NULL,
  updated_by     uuid
);

CREATE INDEX stations_operator_id_idx ON stations (operator_id, id);

CREATE TABLE pos_devices (
  id                       uuid PRIMARY KEY,
  operator_id              uuid NOT NULL REFERENCES station_operators (id),
  station_id               uuid NOT NULL REFERENCES stations (id),
  code                     text NOT NULL,
  label                    text,
  status                   text NOT NULL CHECK (status IN ('PENDING_ACTIVATION', 'ACTIVE', 'REVOKED')),
  -- One-time activation code, hashed like a password; redeemed by the device in B5.
  activation_code_hash     text,
  activation_expires_at    timestamptz,
  activation_attempts      integer NOT NULL DEFAULT 0,
  hardware_id              text,
  bound_at                 timestamptz,
  last_sync_at             timestamptz,
  revoked_at               timestamptz,
  revoked_reason           text,
  created_at               timestamptz NOT NULL,
  created_by               uuid,
  updated_at               timestamptz NOT NULL,
  updated_by               uuid
);

CREATE UNIQUE INDEX pos_devices_code_key ON pos_devices (code);
CREATE INDEX pos_devices_operator_id_idx ON pos_devices (operator_id);
CREATE INDEX pos_devices_station_id_idx ON pos_devices (station_id, id);

CREATE TABLE agents (
  id                  uuid PRIMARY KEY,
  operator_id         uuid NOT NULL REFERENCES station_operators (id),
  station_id          uuid NOT NULL REFERENCES stations (id),
  -- Printed on the staff card; the POS sign-in is Worker ID + 6-digit PIN (PRD 6.1).
  worker_id           text NOT NULL,
  full_name           text NOT NULL,
  phone               text,
  pin_hash            text NOT NULL,
  must_change_pin     boolean NOT NULL,
  failed_pin_count    integer NOT NULL DEFAULT 0,
  locked_until        timestamptz,
  status              text NOT NULL CHECK (status IN ('ACTIVE', 'SUSPENDED', 'DEACTIVATED')),
  created_at          timestamptz NOT NULL,
  created_by          uuid,
  updated_at          timestamptz NOT NULL,
  updated_by          uuid
);

CREATE UNIQUE INDEX agents_worker_id_key ON agents (worker_id);
CREATE INDEX agents_operator_id_idx ON agents (operator_id);
CREATE INDEX agents_station_id_idx ON agents (station_id, id);

-- -------------------------------------------------------------------------------------- pricing

CREATE TABLE fuel_prices (
  id               uuid PRIMARY KEY,
  fuel_type        text NOT NULL CHECK (fuel_type IN ('GAS_80', 'GAS_92', 'GAS_95', 'DIESEL', 'CNG')),
  -- NULL: the national price. Otherwise an Ops override for one station (roadmap delta M1).
  station_id       uuid REFERENCES stations (id),
  price_piastres   bigint NOT NULL CHECK (price_piastres > 0),
  -- Prices are scheduled ahead; devices apply them at this time, offline too.
  effective_from   timestamptz NOT NULL,
  note             text,
  created_at       timestamptz NOT NULL,
  created_by       uuid
);

CREATE INDEX fuel_prices_lookup_idx ON fuel_prices (fuel_type, station_id, effective_from DESC);
-- Two prices for the same fuel, place and instant would make the price at that instant ambiguous.
CREATE UNIQUE INDEX fuel_prices_one_per_instant
  ON fuel_prices (fuel_type, coalesce(station_id, '00000000-0000-0000-0000-000000000000'::uuid), effective_from);
CREATE INDEX fuel_prices_station_id_idx ON fuel_prices (station_id);

-- ----------------------------------------------------------------------------- grants and RLS

GRANT SELECT, INSERT, UPDATE ON companies, departments, vehicles, rfid_tags, limit_rules,
  station_operators, stations, pos_devices, agents TO zawed_app;
GRANT SELECT, INSERT ON fuel_prices TO zawed_app;

-- Company-owned rows: a platform scope sees all; a company scope sees its own.
ALTER TABLE companies ENABLE ROW LEVEL SECURITY;
ALTER TABLE companies FORCE ROW LEVEL SECURITY;
CREATE POLICY companies_by_scope ON companies
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND id = app_scope_id()))
  WITH CHECK (app_is_platform());

ALTER TABLE departments ENABLE ROW LEVEL SECURITY;
ALTER TABLE departments FORCE ROW LEVEL SECURITY;
CREATE POLICY departments_by_scope ON departments
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id()))
  WITH CHECK (app_is_platform());

ALTER TABLE vehicles ENABLE ROW LEVEL SECURITY;
ALTER TABLE vehicles FORCE ROW LEVEL SECURITY;
CREATE POLICY vehicles_by_scope ON vehicles
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id()))
  WITH CHECK (app_is_platform());

ALTER TABLE rfid_tags ENABLE ROW LEVEL SECURITY;
ALTER TABLE rfid_tags FORCE ROW LEVEL SECURITY;
CREATE POLICY rfid_tags_by_scope ON rfid_tags
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id()))
  WITH CHECK (app_is_platform());

ALTER TABLE limit_rules ENABLE ROW LEVEL SECURITY;
ALTER TABLE limit_rules FORCE ROW LEVEL SECURITY;
CREATE POLICY limit_rules_by_scope ON limit_rules
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id()))
  WITH CHECK (app_is_platform());

-- Operator-owned rows: a platform scope sees all; an operator scope sees its own.
ALTER TABLE station_operators ENABLE ROW LEVEL SECURITY;
ALTER TABLE station_operators FORCE ROW LEVEL SECURITY;
CREATE POLICY station_operators_by_scope ON station_operators
  USING (app_is_platform() OR (app_scope_type() = 'OPERATOR' AND id = app_scope_id()))
  WITH CHECK (app_is_platform());

ALTER TABLE stations ENABLE ROW LEVEL SECURITY;
ALTER TABLE stations FORCE ROW LEVEL SECURITY;
CREATE POLICY stations_by_scope ON stations
  USING (app_is_platform() OR (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id()))
  WITH CHECK (app_is_platform());

ALTER TABLE pos_devices ENABLE ROW LEVEL SECURITY;
ALTER TABLE pos_devices FORCE ROW LEVEL SECURITY;
CREATE POLICY pos_devices_by_scope ON pos_devices
  USING (app_is_platform() OR (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id()))
  WITH CHECK (app_is_platform());

ALTER TABLE agents ENABLE ROW LEVEL SECURITY;
ALTER TABLE agents FORCE ROW LEVEL SECURITY;
CREATE POLICY agents_by_scope ON agents
  USING (app_is_platform() OR (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id()))
  WITH CHECK (app_is_platform());

-- Prices are public to every signed-in scope (they are printed on receipts); only the platform
-- schedules them. Without any scope (an anonymous transaction) nothing is visible.
ALTER TABLE fuel_prices ENABLE ROW LEVEL SECURITY;
ALTER TABLE fuel_prices FORCE ROW LEVEL SECURITY;
CREATE POLICY fuel_prices_read ON fuel_prices FOR SELECT USING (app_scope_type() IS NOT NULL);
CREATE POLICY fuel_prices_schedule ON fuel_prices FOR INSERT WITH CHECK (app_is_platform());

-- Every child row must stay inside its parent's tenant: a vehicle's department, a tag's vehicle,
-- a limit's vehicle, a device's or agent's station. Checked by the database, not trusted to code.
CREATE FUNCTION check_same_tenant() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
  AS $$
  DECLARE
    v_parent uuid;
  BEGIN
    IF TG_TABLE_NAME = 'vehicles' THEN
      IF NEW.department_id IS NOT NULL THEN
        SELECT company_id INTO v_parent FROM departments WHERE id = NEW.department_id;
        IF v_parent IS DISTINCT FROM NEW.company_id THEN
          RAISE EXCEPTION 'department % is not in company %', NEW.department_id, NEW.company_id
            USING ERRCODE = 'check_violation';
        END IF;
      END IF;
    ELSIF TG_TABLE_NAME IN ('rfid_tags', 'limit_rules') THEN
      SELECT company_id INTO v_parent FROM vehicles WHERE id = NEW.vehicle_id;
      IF v_parent IS DISTINCT FROM NEW.company_id THEN
        RAISE EXCEPTION 'vehicle % is not in company %', NEW.vehicle_id, NEW.company_id
          USING ERRCODE = 'check_violation';
      END IF;
    ELSIF TG_TABLE_NAME IN ('pos_devices', 'agents') THEN
      SELECT operator_id INTO v_parent FROM stations WHERE id = NEW.station_id;
      IF v_parent IS DISTINCT FROM NEW.operator_id THEN
        RAISE EXCEPTION 'station % is not run by operator %', NEW.station_id, NEW.operator_id
          USING ERRCODE = 'check_violation';
      END IF;
    END IF;
    RETURN NEW;
  END
  $$;

REVOKE ALL ON FUNCTION check_same_tenant() FROM PUBLIC;

CREATE TRIGGER vehicles_same_tenant BEFORE INSERT OR UPDATE ON vehicles
  FOR EACH ROW EXECUTE FUNCTION check_same_tenant();
CREATE TRIGGER rfid_tags_same_tenant BEFORE INSERT OR UPDATE ON rfid_tags
  FOR EACH ROW EXECUTE FUNCTION check_same_tenant();
CREATE TRIGGER limit_rules_same_tenant BEFORE INSERT OR UPDATE ON limit_rules
  FOR EACH ROW EXECUTE FUNCTION check_same_tenant();
CREATE TRIGGER pos_devices_same_tenant BEFORE INSERT OR UPDATE ON pos_devices
  FOR EACH ROW EXECUTE FUNCTION check_same_tenant();
CREATE TRIGGER agents_same_tenant BEFORE INSERT OR UPDATE ON agents
  FOR EACH ROW EXECUTE FUNCTION check_same_tenant();
