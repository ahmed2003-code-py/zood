-- Production hardening P0 (audit F3; ADR 0022): request idempotency for creates that have no natural
-- key, starting with companies and wallet adjustments. A client sends an Idempotency-Key header;
-- the first request with a key creates the resource and records the key, a repeat with the same
-- body gets the same resource back, and a repeat with a different body is refused (409). The key
-- row is written in the same transaction as the resource, so a rolled-back create frees its key,
-- and two concurrent requests with one key wait on the unique index instead of both creating.

CREATE TABLE idempotency_keys (
  actor_id      uuid NOT NULL,
  operation     text NOT NULL CHECK (operation ~ '^[a-z][a-z0-9_.]{1,63}$'),
  key           text NOT NULL CHECK (length(key) BETWEEN 8 AND 200),
  request_hash  text NOT NULL CHECK (request_hash ~ '^[0-9a-f]{64}$'),
  resource_id   uuid NOT NULL,
  created_at    timestamptz NOT NULL,
  PRIMARY KEY (actor_id, operation, key)
);

CREATE INDEX idempotency_keys_created_at_idx ON idempotency_keys (created_at);

COMMENT ON TABLE idempotency_keys IS
  'Idempotency-Key per user and operation: the request body hash and the resource it created. Append only.';

GRANT SELECT, INSERT ON idempotency_keys TO zawed_app;

-- A user sees and writes only their own keys: a key is never a way to read another user's resource.
ALTER TABLE idempotency_keys ENABLE ROW LEVEL SECURITY;
CREATE POLICY idempotency_keys_own ON idempotency_keys
  USING (actor_id = app_user_id()) WITH CHECK (actor_id = app_user_id());
