-- Data-key rotation (B7 W9, ADR 0027).
--
-- Encrypted values (platform.crypto.DataEncryptor) start with the id of the key that wrote them. A
-- rotation makes a new key current; the job platform.crypto.DataKeyRotation then reads each value
-- still under an older key and writes it again under the current one. This migration gives the job
-- what it needs and nothing more:
--
--  0. data_key_state: the deployment's current key id and the fingerprint of its lookup key. Only the
--     owner writes it, through Flyway's own connection at every start (platform.crypto.DataKeyState);
--     the runtime role has no grant on it. A start whose lookup key is not the one the stored hashes
--     were made with is refused there.
--  1. data_key_id(value): the key id byte, for the job's queries.
--  2. data_key_rekeyed(old, new): the one shape a re-encryption has. The plaintext and the row binding
--     are the same, so the length is the same (GCM does not pad), the old value is under an older key,
--     and the new one is under exactly the deployment's current key (0). So outside a rotation, when
--     every value is under the current key, nothing can be rekeyed at all, and a value is never
--     written back under an older key (the key an attacker would hold after a leak).
--  3. The guards that freeze a tag's UID (V14) and a bank account's number (V9) now let exactly that
--     change through, when nothing else in the row changes in the same update. Every other change of
--     those columns is still refused, for every role, the owner included.
--  4. The runtime role may write a bank account's ciphertext column (it could only record decisions);
--     the guard above decides what it may write there, and every change of it is audited by the
--     database itself (an append-only audit_log row the role cannot avoid or remove).
--
-- What the database cannot tell: during a rotation, a different value of the same length under the
-- current key, written by someone who holds that key and a runtime session, over a row still under
-- an older key. Such a tag is left out of the offline snapshot (its UID no longer matches uid_lookup,
-- which is made with the lookup key, not the data key); such a bank account change leaves its audit
-- row. ADR 0027 lists this residual risk.
--
-- users.mfa_secret_encrypted and mfa_challenges.pending_secret_encrypted have no guard; the job writes
-- only the ciphertext there too, never updated_at.

-- ----------------------------------------------------------------------------------------------
-- 0. The deployment's key state
-- ----------------------------------------------------------------------------------------------

CREATE TABLE data_key_state (
  singleton              boolean PRIMARY KEY DEFAULT true CHECK (singleton),
  current_key_id         smallint NOT NULL CHECK (current_key_id BETWEEN 1 AND 255),
  lookup_key_fingerprint text NOT NULL CHECK (lookup_key_fingerprint ~ '^[0-9a-f]{64}$'),
  fingerprint_recorded_at timestamptz NOT NULL,
  updated_at             timestamptz NOT NULL
);

COMMENT ON TABLE data_key_state IS
  'One row: the current data key id and an HMAC fingerprint of the lookup key, written by the owner at '
  'every start (DataKeyState, ADR 0027). The runtime role has no grant; it reads the id through '
  'data_key_current_id().';

REVOKE ALL ON data_key_state FROM PUBLIC;

-- The guards run as the role that updates (zawed_app), which may not read the table itself.
CREATE FUNCTION data_key_current_id() RETURNS integer
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp
  AS $$ SELECT current_key_id::integer FROM data_key_state $$;

COMMENT ON FUNCTION data_key_current_id() IS
  'The deployment''s current data key id; null before the first start has recorded it (ADR 0027).';

REVOKE ALL ON FUNCTION data_key_current_id() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION data_key_current_id() TO zawed_app;

-- ----------------------------------------------------------------------------------------------
-- 1, 2. The key id and the shape of a re-encryption
-- ----------------------------------------------------------------------------------------------

CREATE FUNCTION data_key_id(p_value bytea) RETURNS integer
  LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
  AS $$ SELECT CASE WHEN octet_length(p_value) > 0 THEN get_byte(p_value, 0) END $$;

COMMENT ON FUNCTION data_key_id(bytea) IS
  'The data key id (first byte, 1-255) of a DataEncryptor value; null for an empty value (ADR 0027).';

CREATE FUNCTION data_key_rekeyed(p_old bytea, p_new bytea) RETURNS boolean
  LANGUAGE sql STABLE
  AS $$
    SELECT coalesce(octet_length(p_new) = octet_length(p_old)
                    AND data_key_id(p_old) < data_key_id(p_new)
                    AND data_key_id(p_new) = data_key_current_id(), false)
  $$;

COMMENT ON FUNCTION data_key_rekeyed(bytea, bytea) IS
  'True when p_new can be p_old re-encrypted under the current data key: same length, p_old under an '
  'older key, p_new under exactly the deployment''s current key (ADR 0027).';

REVOKE ALL ON FUNCTION data_key_id(bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION data_key_rekeyed(bytea, bytea) FROM PUBLIC;
-- The job's queries call the first; the guards below call the second as the role that updates.
GRANT EXECUTE ON FUNCTION data_key_id(bytea) TO zawed_app;
GRANT EXECUTE ON FUNCTION data_key_rekeyed(bytea, bytea) TO zawed_app;

-- ----------------------------------------------------------------------------------------------
-- 3a. Tags (V14): the UID columns never change, except a re-encryption of uid_encrypted
-- ----------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fleet_identity_guard() RETURNS trigger
  LANGUAGE plpgsql SET search_path = public, pg_temp
  AS $$
  BEGIN
    IF TG_TABLE_NAME = 'rfid_tags' THEN
      -- A re-encryption under the current data key (ADR 0027): only the ciphertext changes, and only into
      -- the shape of a re-encryption. The UID itself, its lookup hash and its last four stay as they were.
      -- What this cannot check, the offline snapshot does: a UID that does not match uid_lookup is left out.
      IF NEW.uid_encrypted IS DISTINCT FROM OLD.uid_encrypted
         AND data_key_rekeyed(OLD.uid_encrypted, NEW.uid_encrypted)
         AND to_jsonb(NEW) - 'uid_encrypted' = to_jsonb(OLD) - 'uid_encrypted' THEN
        RETURN NEW;
      END IF;
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

-- ----------------------------------------------------------------------------------------------
-- 3b. Bank accounts (V9): nothing changes what an account is, except a re-encryption of its number
-- ----------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION station_bank_accounts_guard() RETURNS trigger
  LANGUAGE plpgsql SET search_path = public, pg_temp
  AS $$
  BEGIN
    -- A re-encryption under the current data key (ADR 0027): the status, the decision and every detail
    -- stay as they were, so it is not a transition and none of the rules below apply to it. A different
    -- account number cannot be written this way without the current data key itself, and only over a
    -- value still under an older key; station_bank_accounts_audit_rekey records every such change.
    IF NEW.account_encrypted IS DISTINCT FROM OLD.account_encrypted
       AND data_key_rekeyed(OLD.account_encrypted, NEW.account_encrypted)
       AND to_jsonb(NEW) - 'account_encrypted' = to_jsonb(OLD) - 'account_encrypted' THEN
      RETURN NEW;
    END IF;
    IF NEW.station_id IS DISTINCT FROM OLD.station_id
       OR NEW.operator_id IS DISTINCT FROM OLD.operator_id
       OR NEW.account_kind IS DISTINCT FROM OLD.account_kind
       OR NEW.account_encrypted IS DISTINCT FROM OLD.account_encrypted
       OR NEW.account_last4 IS DISTINCT FROM OLD.account_last4
       OR NEW.bank_name IS DISTINCT FROM OLD.bank_name
       OR NEW.holder_name IS DISTINCT FROM OLD.holder_name
       OR NEW.bank_letter_reference IS DISTINCT FROM OLD.bank_letter_reference
       OR NEW.requested_by IS DISTINCT FROM OLD.requested_by
       OR NEW.requested_at IS DISTINCT FROM OLD.requested_at THEN
      RAISE EXCEPTION 'bank account % cannot change its station, details or requester', OLD.id
        USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status = 'PENDING' AND NEW.status IN ('ACTIVE', 'REJECTED') THEN
      IF NEW.decided_by IS DISTINCT FROM app_user_id() THEN
        RAISE EXCEPTION 'bank account %: the decision must be recorded for the acting user', OLD.id
          USING ERRCODE = 'insufficient_privilege';
      END IF;
    ELSIF OLD.status = 'ACTIVE' AND NEW.status = 'SUPERSEDED' THEN
      IF NEW.decided_by IS DISTINCT FROM OLD.decided_by
         OR NEW.decided_at IS DISTINCT FROM OLD.decided_at
         OR NEW.decision_reason IS DISTINCT FROM OLD.decision_reason
         OR NEW.payouts_held_until IS DISTINCT FROM OLD.payouts_held_until THEN
        RAISE EXCEPTION 'bank account %: superseding changes nothing about its approval', OLD.id
          USING ERRCODE = 'check_violation';
      END IF;
    ELSE
      RAISE EXCEPTION 'bank account % is % and cannot become %', OLD.id, OLD.status, NEW.status
        USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;

-- ----------------------------------------------------------------------------------------------
-- 4. The runtime role may write the ciphertext column; the guard decides what, and the database
--    audits every change of it
-- ----------------------------------------------------------------------------------------------

GRANT UPDATE (account_encrypted) ON station_bank_accounts TO zawed_app;

-- SECURITY DEFINER: the row is written whatever the caller's scope, so no session that may change
-- the column can do it without leaving a trace in the append-only log. Ids and key ids only, never a
-- value. The rotation job has no user, so its rows have no actor.
CREATE FUNCTION station_bank_accounts_audit_rekey() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
  AS $$
  BEGIN
    INSERT INTO audit_log (id, occurred_at, actor_id, actor_role, action, entity_type, entity_id,
                           scope_type, scope_id, metadata)
      VALUES (uuidv7(), now(), app_user_id(), NULL, 'BANK_ACCOUNT_REKEYED', 'STATION_BANK_ACCOUNT', NEW.id,
              'OPERATOR', NEW.operator_id,
              jsonb_build_object('stationId', NEW.station_id, 'fromKeyId', data_key_id(OLD.account_encrypted),
                                 'toKeyId', data_key_id(NEW.account_encrypted)));
    RETURN NULL;
  END
  $$;

REVOKE ALL ON FUNCTION station_bank_accounts_audit_rekey() FROM PUBLIC;

CREATE TRIGGER station_bank_accounts_audit_rekey AFTER UPDATE OF account_encrypted ON station_bank_accounts
  FOR EACH ROW WHEN (NEW.account_encrypted IS DISTINCT FROM OLD.account_encrypted)
  EXECUTE FUNCTION station_bank_accounts_audit_rekey();
