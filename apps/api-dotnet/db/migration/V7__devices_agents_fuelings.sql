-- Phase B4: the device channel (ADR 0010), agent sessions on a device, fuelings and hard alerts.
--
-- Rules from V1, V3 and V4 apply: every scoped table carries its tenant id for its RLS policy; the
-- runtime role gets only what it needs; money and fuelings are append-only.

-- ------------------------------------------------------------------------------- device channel

-- Activation registers the device's own key pair (ADR 0010): the server keeps the public key and
-- only ever trusts a request the device signed with the private one.
ALTER TABLE pos_devices
  ADD COLUMN public_key bytea,
  ADD COLUMN key_algorithm text CHECK (key_algorithm IN ('EC_P256')),
  ADD CONSTRAINT pos_devices_active_has_key CHECK (status <> 'ACTIVE' OR public_key IS NOT NULL);

-- A signed token request carries a nonce; a nonce is accepted once per device, so a captured
-- request cannot be replayed. Rows older than the timestamp window are useless and may be purged.
CREATE TABLE device_nonces (
  device_id    uuid NOT NULL REFERENCES pos_devices (id),
  nonce        text NOT NULL CHECK (length(nonce) BETWEEN 16 AND 128),
  created_at   timestamptz NOT NULL,
  PRIMARY KEY (device_id, nonce)
);

CREATE INDEX device_nonces_created_at_idx ON device_nonces (created_at);

-- An agent signed in on one device. The token is random and stored as SHA-256 only. One live
-- session per agent (PRD: no credential sharing): signing in elsewhere ends the previous one.
CREATE TABLE agent_sessions (
  id             uuid PRIMARY KEY,
  agent_id       uuid NOT NULL REFERENCES agents (id),
  device_id      uuid NOT NULL REFERENCES pos_devices (id),
  operator_id    uuid NOT NULL REFERENCES station_operators (id),
  station_id     uuid NOT NULL REFERENCES stations (id),
  token_hash     text NOT NULL,
  created_at     timestamptz NOT NULL,
  last_seen_at   timestamptz NOT NULL,
  expires_at     timestamptz NOT NULL,
  revoked_at     timestamptz,
  revoke_reason  text
);

CREATE UNIQUE INDEX agent_sessions_token_hash_key ON agent_sessions (token_hash);
CREATE UNIQUE INDEX agent_sessions_one_live_per_agent ON agent_sessions (agent_id) WHERE revoked_at IS NULL;
CREATE INDEX agent_sessions_device_id_idx ON agent_sessions (device_id);

-- ------------------------------------------------------------------------------------- fuelings

-- Short human references printed on the receipt (plan ADR 0005: short references from sequences).
CREATE SEQUENCE fueling_reference_seq START 10001;

-- One row per fueling attempt the engine decided: COMPLETED or DECLINED. Never updated.
CREATE TABLE fuelings (
  id                     uuid PRIMARY KEY,
  client_txn_id          uuid NOT NULL,
  reference              text NOT NULL,
  status                 text NOT NULL CHECK (status IN ('COMPLETED', 'DECLINED')),
  decline_reason         text CHECK (decline_reason IN ('COOLDOWN', 'TAG_INACTIVE', 'VEHICLE_INACTIVE',
                           'COMPANY_FROZEN', 'FUEL_TYPE_MISMATCH', 'FUEL_NOT_AVAILABLE', 'LIMIT_EXCEEDED',
                           'INSUFFICIENT_BALANCE')),
  -- ONLINE: decided now at the pump. OFFLINE (B5): captured on the device, ingested at sync.
  origin                 text NOT NULL CHECK (origin IN ('ONLINE', 'OFFLINE')),
  company_id             uuid NOT NULL REFERENCES companies (id),
  vehicle_id             uuid NOT NULL REFERENCES vehicles (id),
  tag_id                 uuid NOT NULL REFERENCES rfid_tags (id),
  operator_id            uuid NOT NULL REFERENCES station_operators (id),
  station_id             uuid NOT NULL REFERENCES stations (id),
  device_id              uuid NOT NULL REFERENCES pos_devices (id),
  agent_id               uuid NOT NULL REFERENCES agents (id),
  fuel_type              text NOT NULL CHECK (fuel_type IN ('GAS_80', 'GAS_92', 'GAS_95', 'DIESEL', 'CNG')),
  volume_ml              bigint NOT NULL CHECK (volume_ml > 0),
  unit_price_piastres    bigint NOT NULL CHECK (unit_price_piastres >= 0),
  amount_piastres        bigint NOT NULL CHECK (amount_piastres >= 0),
  billable               boolean NOT NULL,
  -- The instant the rules were evaluated at (server time online, device time offline); limit
  -- periods are counted on it, in the station's zone.
  evaluated_at           timestamptz NOT NULL,
  device_at              timestamptz NOT NULL,
  received_at            timestamptz NOT NULL,
  receipt_locale         text NOT NULL CHECK (receipt_locale IN ('ar', 'en')),
  -- What the receipt printed, as it was at the pump. Each audience reads its fuelings without
  -- seeing the other's master data (a company cannot read stations, an operator cannot read
  -- companies), and a later rename never rewrites a past receipt.
  plate                  text NOT NULL,
  company_name_ar        text NOT NULL,
  company_name_en        text NOT NULL,
  station_name_ar        text NOT NULL,
  station_name_en        text NOT NULL,
  agent_worker_id        text NOT NULL,
  ledger_transaction_id  uuid REFERENCES ledger_transactions (id),
  CONSTRAINT fuelings_declined_has_reason CHECK ((status = 'DECLINED') = (decline_reason IS NOT NULL)),
  -- Money moves exactly for a completed, billable fueling with a price.
  CONSTRAINT fuelings_posted_iff_charged CHECK (
    (ledger_transaction_id IS NOT NULL) = (status = 'COMPLETED' AND billable AND amount_piastres > 0))
);

CREATE UNIQUE INDEX fuelings_client_txn_id_key ON fuelings (client_txn_id);
CREATE UNIQUE INDEX fuelings_reference_key ON fuelings (reference);
CREATE UNIQUE INDEX fuelings_ledger_transaction_key ON fuelings (ledger_transaction_id) WHERE ledger_transaction_id IS NOT NULL;
-- Limit usage: completed fuelings of one vehicle since a period start.
CREATE INDEX fuelings_vehicle_usage_idx ON fuelings (vehicle_id, evaluated_at) WHERE status = 'COMPLETED';
CREATE INDEX fuelings_company_id_idx ON fuelings (company_id, id);
CREATE INDEX fuelings_station_id_idx ON fuelings (station_id, id);
CREATE INDEX fuelings_operator_id_idx ON fuelings (operator_id, id);

-- --------------------------------------------------------------------------------- hard alerts

-- Raised by the engine in the fueling's own transaction (PRD 10): never blocking, always visible
-- to Operations, some also to the company.
CREATE TABLE alerts (
  id               uuid PRIMARY KEY,
  type             text NOT NULL CHECK (type IN ('TANK_CAPACITY_EXCEEDED', 'VIP_CEILING_PASSED', 'LOW_BALANCE')),
  severity         text NOT NULL CHECK (severity IN ('WARNING', 'CRITICAL')),
  status           text NOT NULL CHECK (status IN ('OPEN', 'ACKNOWLEDGED')),
  company_id       uuid REFERENCES companies (id),
  operator_id      uuid REFERENCES station_operators (id),
  station_id       uuid REFERENCES stations (id),
  vehicle_id       uuid REFERENCES vehicles (id),
  fueling_id       uuid REFERENCES fuelings (id),
  details          jsonb NOT NULL DEFAULT '{}'::jsonb,
  -- The VIP ceiling is invisible to the company (PRD 4.5): such an alert is for Operations only.
  company_visible  boolean NOT NULL,
  created_at       timestamptz NOT NULL,
  acknowledged_by  uuid,
  acknowledged_at  timestamptz,
  CONSTRAINT alerts_acknowledged CHECK ((status = 'ACKNOWLEDGED') = (acknowledged_at IS NOT NULL))
);

CREATE INDEX alerts_status_idx ON alerts (status, id);
CREATE INDEX alerts_company_id_idx ON alerts (company_id, id);

-- --------------------------------------------------------------------------------- grants and RLS

GRANT SELECT, INSERT ON device_nonces TO zawed_app;                          -- append only
GRANT SELECT, INSERT ON fuelings TO zawed_app;                               -- never updated
GRANT USAGE ON SEQUENCE fueling_reference_seq TO zawed_app;
GRANT SELECT, INSERT ON agent_sessions TO zawed_app;
GRANT UPDATE (last_seen_at, revoked_at, revoke_reason) ON agent_sessions TO zawed_app;
GRANT SELECT, INSERT ON alerts TO zawed_app;
GRANT UPDATE (status, acknowledged_by, acknowledged_at) ON alerts TO zawed_app;

-- The device channel and the engine run in an explicit platform scope after the device and agent
-- are verified (B4 contract §1): these tables are platform-only.
ALTER TABLE device_nonces ENABLE ROW LEVEL SECURITY;
CREATE POLICY device_nonces_platform ON device_nonces USING (app_is_platform()) WITH CHECK (app_is_platform());

ALTER TABLE agent_sessions ENABLE ROW LEVEL SECURITY;
CREATE POLICY agent_sessions_platform ON agent_sessions USING (app_is_platform()) WITH CHECK (app_is_platform());

-- A company sees its own fuelings; an operator the fuelings at its stations; only the platform
-- writes them.
ALTER TABLE fuelings ENABLE ROW LEVEL SECURITY;
CREATE POLICY fuelings_by_scope ON fuelings
  USING (app_is_platform()
         OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id())
         OR (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id()))
  WITH CHECK (app_is_platform());

-- A company sees the alerts about it that are meant for it; only the platform writes and acknowledges.
ALTER TABLE alerts ENABLE ROW LEVEL SECURITY;
CREATE POLICY alerts_by_scope ON alerts
  USING (app_is_platform()
         OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id() AND company_visible))
  WITH CHECK (app_is_platform());
