-- Data-key rotation (B7 W9, ADR 0027).
--
-- Encrypted values (platform.crypto.DataEncryptor) start with the id of the key that wrote them. A
-- rotation makes a new key current; the job platform.crypto.DataKeyRotation then reads each value
-- still under an older key and writes it again under the current one. This migration gives the job
-- what it needs and nothing more:
--
--  1. data_key_id(value): the key id byte, for the job's queries.
--  2. data_key_rekeyed(old, new): the one shape a re-encryption has. The plaintext and the row binding
--     are the same, so the length is the same (GCM does not pad), and the key id only goes up: a
--     value is never written back under an older key, which is the key an attacker would hold after
--     a leak.
--  3. The guards that freeze a tag's UID (V14) and a bank account's number (V9) now let exactly that
--     change through, when nothing else in the row changes in the same update. Every other change of
--     those columns is still refused, for every role, the owner included.
--  4. The runtime role may write a bank account's ciphertext column (it could only record decisions);
--     the guard above decides what it may write there.
--
-- users.mfa_secret_encrypted and mfa_challenges.pending_secret_encrypted have no guard; the job writes
-- only the ciphertext there too, never updated_at.

-- ----------------------------------------------------------------------------------------------
-- 1, 2. The key id and the shape of a re-encryption
-- ----------------------------------------------------------------------------------------------

CREATE FUNCTION data_key_id(p_value bytea) RETURNS integer
  LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
  AS $$ SELECT CASE WHEN octet_length(p_value) > 0 THEN get_byte(p_value, 0) END $$;

COMMENT ON FUNCTION data_key_id(bytea) IS
  'The data key id (first byte, 1-255) of a DataEncryptor value; null for an empty value (ADR 0027).';

CREATE FUNCTION data_key_rekeyed(p_old bytea, p_new bytea) RETURNS boolean
  LANGUAGE sql IMMUTABLE PARALLEL SAFE
  AS $$
    SELECT coalesce(octet_length(p_new) = octet_length(p_old) AND data_key_id(p_new) > data_key_id(p_old), false)
  $$;

COMMENT ON FUNCTION data_key_rekeyed(bytea, bytea) IS
  'True when p_new can be p_old re-encrypted under a newer data key: same length, higher key id (ADR 0027).';

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
      -- A re-encryption under a newer data key (ADR 0027): only the ciphertext changes, and only into
      -- the shape of a re-encryption. The UID itself, its lookup hash and its last four stay as they were.
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
    -- A re-encryption under a newer data key (ADR 0027): the status, the decision and every detail stay
    -- as they were, so it is not a transition and none of the rules below apply to it. A different
    -- account number cannot be written this way without the current data key itself.
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
-- 4. The runtime role may write the ciphertext column; the guard decides what
-- ----------------------------------------------------------------------------------------------

GRANT UPDATE (account_encrypted) ON station_bank_accounts TO zawed_app;
