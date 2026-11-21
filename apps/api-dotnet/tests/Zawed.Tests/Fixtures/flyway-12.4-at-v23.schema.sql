--
-- PostgreSQL database dump
--

\restrict dRAk496aPeaXY42YEJjSEwwhsdwU2jx5LGeexdVALGBh7p1SEX6khmRl6BC9Omo

-- Dumped from database version 18.6 (Debian 18.6-1.pgdg13+2)
-- Dumped by pg_dump version 18.6 (Debian 18.6-1.pgdg13+2)

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: SCHEMA public; Type: COMMENT; Schema: -; Owner: pg_database_owner
--

COMMENT ON SCHEMA public IS 'ZAWED core schema. Money: bigint piastres. Volume: bigint millilitres. Time: timestamptz in UTC. Keys: uuid v7.';


--
-- Name: auth_session_row; Type: TYPE; Schema: public; Owner: postgres
--

CREATE TYPE public.auth_session_row AS (
	session_id uuid,
	user_id uuid,
	door text,
	refresh_token_hash text,
	previous_refresh_token_hash text,
	rotated_at timestamp with time zone,
	created_at timestamp with time zone,
	last_seen_at timestamp with time zone,
	expires_at timestamp with time zone,
	absolute_expires_at timestamp with time zone,
	revoked_at timestamp with time zone,
	email text,
	full_name text,
	role text,
	scope_type text,
	scope_id uuid,
	status text,
	must_change_password boolean,
	mfa_enabled boolean
);


ALTER TYPE public.auth_session_row OWNER TO postgres;

--
-- Name: app_is_platform(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.app_is_platform() RETURNS boolean
    LANGUAGE sql STABLE PARALLEL SAFE
    AS $$ SELECT coalesce(app_scope_type() = 'PLATFORM', false) $$;


ALTER FUNCTION public.app_is_platform() OWNER TO postgres;

--
-- Name: app_scope_id(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.app_scope_id() RETURNS uuid
    LANGUAGE sql STABLE PARALLEL SAFE
    AS $$ SELECT nullif(current_setting('app.scope_id', true), '')::uuid $$;


ALTER FUNCTION public.app_scope_id() OWNER TO postgres;

--
-- Name: app_scope_type(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.app_scope_type() RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE
    AS $$ SELECT nullif(current_setting('app.scope_type', true), '') $$;


ALTER FUNCTION public.app_scope_type() OWNER TO postgres;

--
-- Name: app_user_id(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.app_user_id() RETURNS uuid
    LANGUAGE sql STABLE PARALLEL SAFE
    AS $$ SELECT nullif(current_setting('app.user_id', true), '')::uuid $$;


ALTER FUNCTION public.app_user_id() OWNER TO postgres;

--
-- Name: app_user_role(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.app_user_role() RETURNS text
    LANGUAGE sql STABLE PARALLEL SAFE
    AS $$ SELECT u.role FROM users u WHERE u.id = app_user_id() $$;


ALTER FUNCTION public.app_user_role() OWNER TO postgres;

--
-- Name: auth_bootstrap_super_admin(uuid, text, text, text, timestamp with time zone, uuid, text, text); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.auth_bootstrap_super_admin(p_id uuid, p_email text, p_full_name text, p_password_hash text, p_now timestamp with time zone, p_audit_id uuid, p_request_id text, p_ip_prefix text) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('auth_bootstrap_super_admin'));
    IF EXISTS (SELECT 1 FROM users WHERE role = 'SUPER_ADMIN') THEN
      RETURN false;
    END IF;
    INSERT INTO users (id, email, full_name, password_hash, role, scope_type, scope_id, status,
                       must_change_password, password_changed_at, created_at, created_by,
                       updated_at, updated_by)
      VALUES (p_id, p_email, p_full_name, p_password_hash, 'SUPER_ADMIN', 'PLATFORM', NULL,
              'ACTIVE', false, p_now, p_now, NULL, p_now, NULL);
    INSERT INTO audit_log (id, occurred_at, actor_id, actor_role, action, entity_type, entity_id,
                           scope_type, scope_id, metadata, request_id, ip_prefix)
      VALUES (p_audit_id, p_now, p_id, 'SUPER_ADMIN', 'SUPER_ADMIN_BOOTSTRAPPED', 'USER', p_id,
              'PLATFORM', NULL, '{}'::jsonb, p_request_id, p_ip_prefix);
    RETURN true;
  END
  $$;


ALTER FUNCTION public.auth_bootstrap_super_admin(p_id uuid, p_email text, p_full_name text, p_password_hash text, p_now timestamp with time zone, p_audit_id uuid, p_request_id text, p_ip_prefix text) OWNER TO postgres;

--
-- Name: auth_login_lookup(text); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.auth_login_lookup(p_email text) RETURNS TABLE(id uuid, email text, full_name text, password_hash text, role text, scope_type text, scope_id uuid, status text, must_change_password boolean, failed_login_count integer, locked_until timestamp with time zone, mfa_enabled boolean)
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
    SELECT u.id, u.email, u.full_name, u.password_hash, u.role, u.scope_type, u.scope_id,
           u.status, u.must_change_password, u.failed_login_count, u.locked_until, u.mfa_enabled
    FROM users u
    WHERE lower(u.email) = lower(p_email)
  $$;


ALTER FUNCTION public.auth_login_lookup(p_email text) OWNER TO postgres;

--
-- Name: auth_record_login_failure(uuid, boolean, text, integer, integer, timestamp with time zone, uuid, uuid, text, text); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.auth_record_login_failure(p_user_id uuid, p_count boolean, p_reason text, p_max_attempts integer, p_lock_seconds integer, p_now timestamp with time zone, p_audit_id uuid, p_lock_audit_id uuid, p_request_id text, p_ip_prefix text) RETURNS timestamp with time zone
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    v_user         users%ROWTYPE;
    v_failures     integer;
    v_locked_until timestamptz;
    v_locked_now   boolean := false;
  BEGIN
    SELECT * INTO v_user FROM users WHERE id = p_user_id FOR UPDATE;
    IF NOT FOUND THEN
      RETURN NULL;
    END IF;
    v_failures := v_user.failed_login_count;
    v_locked_until := v_user.locked_until;
    IF p_count AND v_user.status = 'ACTIVE' THEN
      v_failures := v_failures + 1;
      IF v_failures >= p_max_attempts THEN
        v_locked_until := p_now + make_interval(secs => p_lock_seconds);
        v_failures := 0; -- the counter starts again once the lock ends
        v_locked_now := true;
      END IF;
      UPDATE users SET failed_login_count = v_failures, locked_until = v_locked_until, updated_at = p_now
        WHERE id = p_user_id;
    END IF;
    INSERT INTO audit_log (id, occurred_at, actor_id, actor_role, action, entity_type, entity_id,
                           scope_type, scope_id, metadata, request_id, ip_prefix)
      VALUES (p_audit_id, p_now, NULL, NULL, 'LOGIN_FAILED', 'USER', p_user_id,
              v_user.scope_type, v_user.scope_id, jsonb_build_object('reason', p_reason),
              p_request_id, p_ip_prefix);
    IF v_locked_now THEN
      INSERT INTO audit_log (id, occurred_at, actor_id, actor_role, action, entity_type, entity_id,
                             scope_type, scope_id, metadata, request_id, ip_prefix)
        VALUES (p_lock_audit_id, p_now, NULL, NULL, 'ACCOUNT_LOCKED', 'USER', p_user_id,
                v_user.scope_type, v_user.scope_id,
                jsonb_build_object('lockedUntil', v_locked_until, 'attempts', p_max_attempts),
                p_request_id, p_ip_prefix);
    END IF;
    RETURN v_locked_until;
  END
  $$;


ALTER FUNCTION public.auth_record_login_failure(p_user_id uuid, p_count boolean, p_reason text, p_max_attempts integer, p_lock_seconds integer, p_now timestamp with time zone, p_audit_id uuid, p_lock_audit_id uuid, p_request_id text, p_ip_prefix text) OWNER TO postgres;

--
-- Name: auth_session_by_id(uuid); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.auth_session_by_id(p_session_id uuid) RETURNS SETOF public.auth_session_row
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
    SELECT s.id, s.user_id, s.door, s.refresh_token_hash, s.previous_refresh_token_hash,
           s.rotated_at, s.created_at, s.last_seen_at, s.expires_at, s.absolute_expires_at,
           s.revoked_at, u.email, u.full_name, u.role, u.scope_type, u.scope_id, u.status,
           u.must_change_password, u.mfa_enabled
    FROM sessions s JOIN users u ON u.id = s.user_id
    WHERE s.id = p_session_id
  $$;


ALTER FUNCTION public.auth_session_by_id(p_session_id uuid) OWNER TO postgres;

--
-- Name: auth_session_by_refresh_hash(text); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.auth_session_by_refresh_hash(p_hash text) RETURNS SETOF public.auth_session_row
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
    SELECT s.id, s.user_id, s.door, s.refresh_token_hash, s.previous_refresh_token_hash,
           s.rotated_at, s.created_at, s.last_seen_at, s.expires_at, s.absolute_expires_at,
           s.revoked_at, u.email, u.full_name, u.role, u.scope_type, u.scope_id, u.status,
           u.must_change_password, u.mfa_enabled
    FROM sessions s JOIN users u ON u.id = s.user_id
    WHERE s.refresh_token_hash = p_hash OR s.previous_refresh_token_hash = p_hash
  $$;


ALTER FUNCTION public.auth_session_by_refresh_hash(p_hash text) OWNER TO postgres;

--
-- Name: auth_super_admin_exists(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.auth_super_admin_exists() RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$ SELECT EXISTS (SELECT 1 FROM users WHERE role = 'SUPER_ADMIN') $$;


ALTER FUNCTION public.auth_super_admin_exists() OWNER TO postgres;

--
-- Name: check_same_tenant(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.check_same_tenant() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
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


ALTER FUNCTION public.check_same_tenant() OWNER TO postgres;

--
-- Name: contact_digits(text); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.contact_digits(p_value text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$
    SELECT regexp_replace(translate(p_value, '٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹', '01234567890123456789'),
                          '[[:space:]\u00A0\u2000-\u200A\u202F\u205F\u3000().\u2010-\u2015-]', '', 'g')
  $$;


ALTER FUNCTION public.contact_digits(p_value text) OWNER TO postgres;

--
-- Name: data_key_current_id(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.data_key_current_id() RETURNS integer
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$ SELECT current_key_id::integer FROM data_key_state $$;


ALTER FUNCTION public.data_key_current_id() OWNER TO postgres;

--
-- Name: FUNCTION data_key_current_id(); Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON FUNCTION public.data_key_current_id() IS 'The deployment''s current data key id; null before the first start has recorded it (ADR 0027).';


--
-- Name: data_key_id(bytea); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.data_key_id(p_value bytea) RETURNS integer
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
    AS $$ SELECT CASE WHEN octet_length(p_value) > 0 THEN get_byte(p_value, 0) END $$;


ALTER FUNCTION public.data_key_id(p_value bytea) OWNER TO postgres;

--
-- Name: FUNCTION data_key_id(p_value bytea); Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON FUNCTION public.data_key_id(p_value bytea) IS 'The data key id (first byte, 1-255) of a DataEncryptor value; null for an empty value (ADR 0027).';


--
-- Name: data_key_rekeyed(bytea, bytea); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.data_key_rekeyed(p_old bytea, p_new bytea) RETURNS boolean
    LANGUAGE sql STABLE
    AS $$
    SELECT coalesce(octet_length(p_new) = octet_length(p_old)
                    AND data_key_id(p_old) < data_key_id(p_new)
                    AND data_key_id(p_new) = data_key_current_id(), false)
  $$;


ALTER FUNCTION public.data_key_rekeyed(p_old bytea, p_new bytea) OWNER TO postgres;

--
-- Name: FUNCTION data_key_rekeyed(p_old bytea, p_new bytea); Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON FUNCTION public.data_key_rekeyed(p_old bytea, p_new bytea) IS 'True when p_new can be p_old re-encrypted under the current data key: same length, p_old under an older key, p_new under exactly the deployment''s current key (ADR 0027).';


--
-- Name: fleet_identity_guard(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.fleet_identity_guard() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public', 'pg_temp'
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


ALTER FUNCTION public.fleet_identity_guard() OWNER TO postgres;

--
-- Name: fueling_reversals_check_insert(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.fueling_reversals_check_insert() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  BEGIN
    IF NOT EXISTS (SELECT 1 FROM fuelings
                    WHERE id = NEW.fueling_id AND company_id = NEW.company_id AND operator_id = NEW.operator_id
                      AND station_id = NEW.station_id AND status = 'COMPLETED'
                      AND ledger_transaction_id IS NOT NULL AND amount_piastres = NEW.amount_piastres) THEN
      RAISE EXCEPTION 'reversal %: fueling % is not a completed, posted fueling of % piastres for these tenants',
        NEW.id, NEW.fueling_id, NEW.amount_piastres USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.status <> 'PENDING' THEN
      RAISE EXCEPTION 'reversal % must start as a request, not %', NEW.id, NEW.status
        USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.requested_by IS DISTINCT FROM app_user_id() THEN
      RAISE EXCEPTION 'reversal %: the requester must be the acting user', NEW.id
        USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
  END
  $$;


ALTER FUNCTION public.fueling_reversals_check_insert() OWNER TO postgres;

--
-- Name: fueling_reversals_guard(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.fueling_reversals_guard() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  BEGIN
    IF OLD.status <> 'PENDING' THEN
      RAISE EXCEPTION 'reversal % is % and can no longer change', OLD.id, OLD.status
        USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.fueling_id IS DISTINCT FROM OLD.fueling_id
       OR NEW.company_id IS DISTINCT FROM OLD.company_id
       OR NEW.operator_id IS DISTINCT FROM OLD.operator_id
       OR NEW.station_id IS DISTINCT FROM OLD.station_id
       OR NEW.amount_piastres IS DISTINCT FROM OLD.amount_piastres
       OR NEW.reason IS DISTINCT FROM OLD.reason
       OR NEW.requested_by IS DISTINCT FROM OLD.requested_by
       OR NEW.requested_at IS DISTINCT FROM OLD.requested_at THEN
      RAISE EXCEPTION 'reversal % cannot change its fueling, amount, reason or requester', OLD.id
        USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.status = 'PENDING' OR NEW.decided_by IS DISTINCT FROM app_user_id() THEN
      RAISE EXCEPTION 'reversal %: a decision must be recorded for the acting user', OLD.id
        USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NEW.status = 'APPROVED' AND NOT EXISTS (
         SELECT 1 FROM ledger_transactions t JOIN fuelings f ON f.id = NEW.fueling_id
          WHERE t.id = NEW.ledger_transaction_id AND t.source_type = 'REVERSAL' AND t.source_id = NEW.id
            AND t.reverses_transaction_id = f.ledger_transaction_id) THEN
      RAISE EXCEPTION 'reversal %: its posting must reverse the posting of fueling %', OLD.id, OLD.fueling_id
        USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;


ALTER FUNCTION public.fueling_reversals_guard() OWNER TO postgres;

--
-- Name: fuelings_refuse_change(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.fuelings_refuse_change() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
  BEGIN
    RAISE EXCEPTION '% is immutable: % is not allowed', TG_TABLE_NAME, TG_OP
      USING ERRCODE = 'insufficient_privilege';
  END
  $$;


ALTER FUNCTION public.fuelings_refuse_change() OWNER TO postgres;

--
-- Name: fuelings_same_tenant(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.fuelings_same_tenant() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
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


ALTER FUNCTION public.fuelings_same_tenant() OWNER TO postgres;

--
-- Name: job_run_count_keys(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.job_run_count_keys() RETURNS text[]
    LANGUAGE sql IMMUTABLE
    SET search_path TO 'public', 'pg_temp'
    AS $$ SELECT ARRAY['columnErrors', 'deleted', 'failed', 'findings', 'mfaChallenges', 'payables', 'reencrypted',
                     'remaining', 'sessions', 'wallets']::text[] $$;


ALTER FUNCTION public.job_run_count_keys() OWNER TO postgres;

--
-- Name: job_run_counts_valid(jsonb); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.job_run_counts_valid(p_counts jsonb) RETURNS boolean
    LANGUAGE plpgsql IMMUTABLE
    SET search_path TO 'public', 'pg_temp'
    AS $_$
  BEGIN
    IF p_counts IS NULL OR jsonb_typeof(p_counts) <> 'object' THEN
      RETURN false;
    END IF;
    RETURN NOT EXISTS (
             SELECT 1 FROM jsonb_each(p_counts) e
              WHERE NOT (e.key = ANY (job_run_count_keys()))
                 OR jsonb_typeof(e.value) <> 'number'
                 OR e.value::text !~ '^[0-9]{1,18}$');
  END
  $_$;


ALTER FUNCTION public.job_run_counts_valid(p_counts jsonb) OWNER TO postgres;

--
-- Name: job_run_latest(text[]); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.job_run_latest(p_names text[]) RETURNS TABLE(job_name text, last_started_at timestamp with time zone, last_finished_at timestamp with time zone, last_outcome text, last_success_at timestamp with time zone, last_success_counts jsonb, last_failure_at timestamp with time zone, last_failure_error_class text)
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
    SELECT n.job, l.started_at, l.finished_at, l.outcome, s.finished_at, s.counts, f.finished_at, f.error_class
      FROM unnest(p_names) AS n(job)
      LEFT JOIN LATERAL (SELECT r.started_at, r.finished_at, r.outcome FROM job_runs r
                          WHERE r.job_name = n.job ORDER BY r.started_at DESC, r.id DESC LIMIT 1) l ON true
      LEFT JOIN LATERAL (SELECT r.finished_at, r.counts FROM job_runs r
                          WHERE r.job_name = n.job AND r.outcome = 'SUCCESS'
                          ORDER BY r.finished_at DESC LIMIT 1) s ON true
      LEFT JOIN LATERAL (SELECT r.finished_at, r.error_class FROM job_runs r
                          WHERE r.job_name = n.job AND r.outcome = 'FAILED'
                          ORDER BY r.finished_at DESC LIMIT 1) f ON true
  $$;


ALTER FUNCTION public.job_run_latest(p_names text[]) OWNER TO postgres;

--
-- Name: job_run_record(uuid, text, text, timestamp with time zone, timestamp with time zone, text, jsonb, text); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.job_run_record(p_id uuid, p_job text, p_instance text, p_started timestamp with time zone, p_finished timestamp with time zone, p_outcome text, p_counts jsonb, p_error_class text) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  BEGIN
    IF p_finished > now() + interval '5 minutes' THEN
      RAISE EXCEPTION 'job_run_record: a run cannot finish in the future' USING ERRCODE = 'check_violation';
    END IF;
    INSERT INTO job_runs (id, job_name, instance_id, started_at, finished_at, outcome, counts, error_class)
      VALUES (p_id, p_job, p_instance, p_started, p_finished, p_outcome, coalesce(p_counts, '{}'::jsonb),
              p_error_class);
  END
  $$;


ALTER FUNCTION public.job_run_record(p_id uuid, p_job text, p_instance text, p_started timestamp with time zone, p_finished timestamp with time zone, p_outcome text, p_counts jsonb, p_error_class text) OWNER TO postgres;

--
-- Name: ledger_check_balanced(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.ledger_check_balanced() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    v_txn      uuid;
    v_debits   bigint;
    v_credits  bigint;
    v_count    integer;
    v_original uuid;
    v_reversal uuid;
  BEGIN
    -- Separate branches, not one CASE: PL/pgSQL plans every field an expression names, and a
    -- ledger_transactions row has no transaction_id.
    IF TG_TABLE_NAME = 'ledger_entries' THEN
      v_txn := NEW.transaction_id;
    ELSE
      v_txn := NEW.id;
    END IF;
    SELECT coalesce(sum(CASE WHEN direction = 'DEBIT' THEN amount_piastres ELSE 0 END), 0),
           coalesce(sum(CASE WHEN direction = 'CREDIT' THEN amount_piastres ELSE 0 END), 0),
           count(*)
      INTO v_debits, v_credits, v_count
      FROM ledger_entries WHERE transaction_id = v_txn;
    IF v_count < 2 THEN
      RAISE EXCEPTION 'ledger transaction % has % entries; at least two are required', v_txn, v_count
        USING ERRCODE = 'check_violation';
    END IF;
    IF v_debits <> v_credits THEN
      RAISE EXCEPTION 'ledger transaction % does not balance: debits % <> credits %', v_txn, v_debits, v_credits
        USING ERRCODE = 'check_violation';
    END IF;

    SELECT reverses_transaction_id INTO v_original FROM ledger_transactions WHERE id = v_txn;
    IF v_original IS NOT NULL THEN
      v_reversal := v_txn;
    ELSE
      SELECT id INTO v_reversal FROM ledger_transactions WHERE reverses_transaction_id = v_txn;
      v_original := v_txn;
    END IF;
    IF v_reversal IS NOT NULL THEN
      IF EXISTS (SELECT 1 FROM ledger_transactions WHERE id = v_original AND source_type = 'REVERSAL') THEN
        RAISE EXCEPTION 'ledger transaction % is a reversal and cannot be reversed', v_original
          USING ERRCODE = 'check_violation';
      END IF;
      IF EXISTS (
           SELECT 1
             FROM (SELECT account_id,
                          sum(CASE direction WHEN 'DEBIT' THEN amount_piastres ELSE -amount_piastres END) AS net
                     FROM ledger_entries
                    WHERE transaction_id IN (v_original, v_reversal)
                    GROUP BY account_id) n
            WHERE n.net <> 0) THEN
        RAISE EXCEPTION 'ledger transaction % does not mirror the transaction % it reverses', v_reversal, v_original
          USING ERRCODE = 'check_violation';
      END IF;
    END IF;
    RETURN NULL;
  END
  $$;


ALTER FUNCTION public.ledger_check_balanced() OWNER TO postgres;

--
-- Name: ledger_entry_check_account(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.ledger_entry_check_account() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    v_kind    text;
    v_company uuid;
    v_cached  bigint;
  BEGIN
    SELECT kind, company_id INTO v_kind, v_company FROM ledger_accounts WHERE id = NEW.account_id;
    IF v_kind IS NULL THEN
      RAISE EXCEPTION 'ledger account % does not exist', NEW.account_id USING ERRCODE = 'foreign_key_violation';
    END IF;
    IF v_company IS DISTINCT FROM NEW.company_id THEN
      RAISE EXCEPTION 'ledger entry company % does not match account %', NEW.company_id, NEW.account_id
        USING ERRCODE = 'check_violation';
    END IF;
    IF v_kind = 'STATION_PAYABLE' THEN
      SELECT balance_piastres INTO v_cached FROM station_payables WHERE account_id = NEW.account_id;
      IF NEW.balance_after_piastres IS NULL OR v_cached IS DISTINCT FROM NEW.balance_after_piastres THEN
        RAISE EXCEPTION 'station payable entry on account % records balance % but the payable holds %',
          NEW.account_id, NEW.balance_after_piastres, v_cached USING ERRCODE = 'check_violation';
      END IF;
    ELSIF NEW.balance_after_piastres < 0 THEN
      RAISE EXCEPTION 'ledger entry on account % records a negative balance %', NEW.account_id,
        NEW.balance_after_piastres USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;


ALTER FUNCTION public.ledger_entry_check_account() OWNER TO postgres;

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: reconciliation_runs; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.reconciliation_runs (
    id uuid NOT NULL,
    trigger text NOT NULL,
    triggered_by uuid,
    started_at timestamp with time zone NOT NULL,
    finished_at timestamp with time zone,
    wallets_checked integer DEFAULT 0 NOT NULL,
    findings_count integer DEFAULT 0 NOT NULL,
    payables_checked integer DEFAULT 0 NOT NULL,
    CONSTRAINT reconciliation_runs_trigger_check CHECK ((trigger = ANY (ARRAY['SCHEDULED'::text, 'MANUAL'::text])))
);


ALTER TABLE public.reconciliation_runs OWNER TO postgres;

--
-- Name: ledger_reconcile(uuid, text, uuid, text, timestamp with time zone, text); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.ledger_reconcile(p_run_id uuid, p_trigger text, p_actor uuid, p_actor_role text, p_now timestamp with time zone, p_request_id text) RETURNS SETOF public.reconciliation_runs
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    v_wallets          integer;
    v_payables         integer;
    v_wallet_findings  integer;
    v_payable_findings integer;
  BEGIN
    IF NOT pg_try_advisory_xact_lock(hashtext('ledger_reconcile')) THEN
      RETURN;
    END IF;
    IF p_trigger = 'SCHEDULED' AND EXISTS (
         SELECT 1 FROM reconciliation_runs
          WHERE trigger = 'SCHEDULED'
            AND (started_at AT TIME ZONE 'Africa/Cairo')::date = (p_now AT TIME ZONE 'Africa/Cairo')::date) THEN
      RETURN;
    END IF;
    INSERT INTO reconciliation_runs (id, trigger, triggered_by, started_at)
      VALUES (p_run_id, p_trigger, p_actor, p_now);
    INSERT INTO reconciliation_findings (id, run_id, company_id, kind, cached_balance_piastres,
                                         ledger_balance_piastres, difference_piastres, created_at)
      SELECT uuidv7(), p_run_id, c.id,
             CASE WHEN w.company_id IS NULL THEN 'MISSING_WALLET' ELSE 'BALANCE_MISMATCH' END,
             w.balance_piastres, l.balance, w.balance_piastres - l.balance, p_now
        FROM companies c
        LEFT JOIN company_wallets w ON w.company_id = c.id
        LEFT JOIN LATERAL (
          SELECT coalesce(sum(CASE e.direction WHEN 'CREDIT' THEN e.amount_piastres ELSE -e.amount_piastres END), 0)
                   AS balance
            FROM ledger_entries e WHERE e.account_id = w.account_id) l ON true
       WHERE w.company_id IS NULL OR w.balance_piastres <> l.balance;
    GET DIAGNOSTICS v_wallet_findings = ROW_COUNT;
    INSERT INTO reconciliation_findings (id, run_id, station_id, kind, cached_balance_piastres,
                                         ledger_balance_piastres, difference_piastres, created_at)
      SELECT uuidv7(), p_run_id, a.station_id,
             CASE WHEN p.station_id IS NULL THEN 'MISSING_PAYABLE' ELSE 'PAYABLE_MISMATCH' END,
             p.balance_piastres, l.balance, p.balance_piastres - l.balance, p_now
        FROM ledger_accounts a
        LEFT JOIN station_payables p ON p.account_id = a.id
        LEFT JOIN LATERAL (
          SELECT coalesce(sum(CASE e.direction WHEN 'CREDIT' THEN e.amount_piastres ELSE -e.amount_piastres END), 0)
                   AS balance
            FROM ledger_entries e WHERE e.account_id = a.id) l ON true
       WHERE a.kind = 'STATION_PAYABLE' AND (p.station_id IS NULL OR p.balance_piastres <> l.balance);
    GET DIAGNOSTICS v_payable_findings = ROW_COUNT;
    SELECT count(*) INTO v_wallets FROM company_wallets;
    SELECT count(*) INTO v_payables FROM station_payables;
    UPDATE reconciliation_runs
       SET finished_at = clock_timestamp(), wallets_checked = v_wallets, payables_checked = v_payables,
           findings_count = v_wallet_findings + v_payable_findings
     WHERE id = p_run_id;
    INSERT INTO audit_log (id, occurred_at, actor_id, actor_role, action, entity_type, entity_id,
                           scope_type, scope_id, metadata, request_id, ip_prefix)
      VALUES (uuidv7(), p_now, p_actor, p_actor_role, 'RECONCILIATION_RUN', 'RECONCILIATION_RUN', p_run_id,
              'PLATFORM', NULL,
              jsonb_build_object('trigger', p_trigger, 'walletsChecked', v_wallets, 'payablesChecked', v_payables,
                                 'findingsCount', v_wallet_findings + v_payable_findings),
              p_request_id, NULL);
    RETURN QUERY SELECT * FROM reconciliation_runs WHERE id = p_run_id;
  END
  $$;


ALTER FUNCTION public.ledger_reconcile(p_run_id uuid, p_trigger text, p_actor uuid, p_actor_role text, p_now timestamp with time zone, p_request_id text) OWNER TO postgres;

--
-- Name: ledger_refuse_change(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.ledger_refuse_change() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
  BEGIN
    RAISE EXCEPTION '% is immutable: % is not allowed', TG_TABLE_NAME, TG_OP
      USING ERRCODE = 'insufficient_privilege';
  END
  $$;


ALTER FUNCTION public.ledger_refuse_change() OWNER TO postgres;

--
-- Name: notification_param_keys(text); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.notification_param_keys(p_type text) RETURNS text[]
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$
    SELECT CASE p_type
      WHEN 'WALLET_LOW_BALANCE' THEN ARRAY['balancePiastres', 'thresholdPiastres', 'transactionId']
      WHEN 'TOPUP_CREDITED' THEN ARRAY['amountPiastres', 'transactionId']
      WHEN 'TOPUP_REJECTED' THEN ARRAY['amountPiastres']
      WHEN 'TICKET_ANSWERED' THEN ARRAY['ticketType']
      WHEN 'TOPUP_AWAITING_APPROVAL' THEN ARRAY['companyId', 'amountPiastres']
      WHEN 'WALLET_ADJUSTMENT_AWAITING_APPROVAL' THEN ARRAY['companyId', 'amountPiastres']
      WHEN 'HARD_ALERT_RAISED' THEN ARRAY['alertType', 'severity', 'companyId', 'operatorId', 'stationId', 'fuelingId']
      WHEN 'TICKET_OPENED' THEN ARRAY['ticketType', 'companyId', 'operatorId']
      WHEN 'PAYOUT_POSTED' THEN ARRAY['stationId', 'amountPiastres']
      WHEN 'BANK_ACCOUNT_CHANGE_APPROVED' THEN ARRAY['stationId']
      WHEN 'BANK_ACCOUNT_CHANGE_REJECTED' THEN ARRAY['stationId']
      ELSE ARRAY[]::text[]
    END
  $$;


ALTER FUNCTION public.notification_param_keys(p_type text) OWNER TO postgres;

--
-- Name: notification_params_valid(text, jsonb); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.notification_params_valid(p_type text, p jsonb) RETURNS boolean
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $_$
    SELECT CASE
      WHEN p IS NULL OR jsonb_typeof(p) <> 'object' THEN false
      ELSE NOT EXISTS (
             SELECT 1 FROM jsonb_each(p) AS e(key, value)
             WHERE e.key <> ALL (notification_param_keys(p_type))
                OR NOT CASE jsonb_typeof(e.value)
                         WHEN 'number' THEN e.value::text ~ '^-?[0-9]{1,19}$'
                         WHEN 'string' THEN (e.value #>> '{}')
                           ~ '^([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[A-Z][A-Z0-9_]{0,63})$'
                         ELSE false
                       END)
    END
  $_$;


ALTER FUNCTION public.notification_params_valid(p_type text, p jsonb) OWNER TO postgres;

--
-- Name: notification_read_marks_forward(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.notification_read_marks_forward() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
  BEGIN
    IF NEW.user_id <> OLD.user_id OR NEW.read_up_to < OLD.read_up_to THEN
      RAISE EXCEPTION 'notification_read_marks: a mark only moves forward' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;


ALTER FUNCTION public.notification_read_marks_forward() OWNER TO postgres;

--
-- Name: payable_hold_releases_check_insert(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.payable_hold_releases_check_insert() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  BEGIN
    IF NOT EXISTS (SELECT 1 FROM fuelings
                    WHERE id = NEW.fueling_id AND station_id = NEW.station_id AND operator_id = NEW.operator_id
                      AND payable_held AND ledger_transaction_id IS NOT NULL
                      AND amount_piastres = NEW.amount_piastres) THEN
      RAISE EXCEPTION 'release %: fueling % is not a held payable of % piastres at station %', NEW.id, NEW.fueling_id,
        NEW.amount_piastres, NEW.station_id USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.released_by IS DISTINCT FROM app_user_id() THEN
      RAISE EXCEPTION 'release %: the releaser must be the acting user', NEW.id
        USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
  END
  $$;


ALTER FUNCTION public.payable_hold_releases_check_insert() OWNER TO postgres;

--
-- Name: payouts_check_insert(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.payouts_check_insert() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  BEGIN
    IF NOT EXISTS (SELECT 1 FROM stations WHERE id = NEW.station_id AND operator_id = NEW.operator_id) THEN
      RAISE EXCEPTION 'payout %: station % is not run by operator %', NEW.id, NEW.station_id, NEW.operator_id
        USING ERRCODE = 'check_violation';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM station_bank_accounts
                    WHERE id = NEW.bank_account_id AND station_id = NEW.station_id AND status = 'ACTIVE') THEN
      RAISE EXCEPTION 'payout %: % is not the verified bank account of station %', NEW.id, NEW.bank_account_id,
        NEW.station_id USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.status = 'CANCELLED' THEN
      RAISE EXCEPTION 'payout % cannot be recorded as cancelled', NEW.id USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.requested_by IS DISTINCT FROM app_user_id() THEN
      RAISE EXCEPTION 'payout %: the recorder must be the acting user', NEW.id
        USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
  END
  $$;


ALTER FUNCTION public.payouts_check_insert() OWNER TO postgres;

--
-- Name: payouts_guard(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.payouts_guard() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public', 'pg_temp'
    AS $$
  BEGIN
    IF OLD.status <> 'PENDING' THEN
      RAISE EXCEPTION 'payout % is % and can no longer change', OLD.id, OLD.status
        USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.station_id IS DISTINCT FROM OLD.station_id
       OR NEW.operator_id IS DISTINCT FROM OLD.operator_id
       OR NEW.bank_account_id IS DISTINCT FROM OLD.bank_account_id
       OR NEW.amount_piastres IS DISTINCT FROM OLD.amount_piastres
       OR NEW.bank_reference IS DISTINCT FROM OLD.bank_reference
       OR NEW.bank_reference_key IS DISTINCT FROM OLD.bank_reference_key
       OR NEW.value_date IS DISTINCT FROM OLD.value_date
       OR NEW.approval_threshold_piastres IS DISTINCT FROM OLD.approval_threshold_piastres
       OR NEW.approval_required IS DISTINCT FROM OLD.approval_required
       OR NEW.requested_by IS DISTINCT FROM OLD.requested_by
       OR NEW.requested_at IS DISTINCT FROM OLD.requested_at THEN
      RAISE EXCEPTION 'payout % cannot change its station, account, amount, reference, date or recorder', OLD.id
        USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.status = 'PENDING' OR NEW.decided_by IS DISTINCT FROM app_user_id() THEN
      RAISE EXCEPTION 'payout %: a decision must be recorded for the acting user', OLD.id
        USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
  END
  $$;


ALTER FUNCTION public.payouts_guard() OWNER TO postgres;

--
-- Name: plate_key_canonical(text); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.plate_key_canonical(p_key text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $_$
    SELECT CASE WHEN p_key ~ '^[0-9]+[^0-9]+$' OR p_key ~ '^[^0-9]+[0-9]+$'
                THEN regexp_replace(p_key, '[^0-9]', '', 'g') || regexp_replace(p_key, '[0-9]', '', 'g')
                ELSE p_key END
  $_$;


ALTER FUNCTION public.plate_key_canonical(p_key text) OWNER TO postgres;

--
-- Name: purge_closed_leads(timestamp with time zone, integer); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.purge_closed_leads(p_before timestamp with time zone, p_limit integer) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    deleted integer;
  BEGIN
    IF p_before IS NULL OR p_before > now() - interval '90 days' THEN
      RAISE EXCEPTION 'purge_closed_leads: % is inside the 90-day minimum retention', p_before
        USING ERRCODE = 'check_violation';
    END IF;
    DELETE FROM leads l
     USING (SELECT id FROM leads WHERE status = 'CLOSED' AND closed_at < p_before
             ORDER BY closed_at LIMIT least(greatest(p_limit, 1), 50000)) old
     WHERE l.id = old.id;
    GET DIAGNOSTICS deleted = ROW_COUNT;
    RETURN deleted;
  END
  $$;


ALTER FUNCTION public.purge_closed_leads(p_before timestamp with time zone, p_limit integer) OWNER TO postgres;

--
-- Name: purge_device_nonces(timestamp with time zone, integer); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.purge_device_nonces(p_before timestamp with time zone, p_limit integer) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
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


ALTER FUNCTION public.purge_device_nonces(p_before timestamp with time zone, p_limit integer) OWNER TO postgres;

--
-- Name: purge_idempotency_keys(timestamp with time zone, integer); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.purge_idempotency_keys(p_before timestamp with time zone, p_limit integer) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    deleted integer;
  BEGIN
    IF p_before IS NULL OR p_before > now() - interval '7 days' THEN
      RAISE EXCEPTION 'purge_idempotency_keys: % is inside the 7-day minimum retention', p_before
        USING ERRCODE = 'check_violation';
    END IF;
    DELETE FROM idempotency_keys k
     USING (SELECT actor_id, operation, key FROM idempotency_keys WHERE created_at < p_before
             ORDER BY created_at LIMIT least(greatest(p_limit, 1), 50000)) old
     WHERE k.actor_id = old.actor_id AND k.operation = old.operation AND k.key = old.key;
    GET DIAGNOSTICS deleted = ROW_COUNT;
    RETURN deleted;
  END
  $$;


ALTER FUNCTION public.purge_idempotency_keys(p_before timestamp with time zone, p_limit integer) OWNER TO postgres;

--
-- Name: purge_job_runs(timestamp with time zone, integer); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.purge_job_runs(p_before timestamp with time zone, p_limit integer) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    deleted integer;
  BEGIN
    IF p_before IS NULL OR p_before > now() - interval '30 days' THEN
      RAISE EXCEPTION 'purge_job_runs: % is inside the 30-day minimum retention', p_before
        USING ERRCODE = 'check_violation';
    END IF;
    DELETE FROM job_runs j
     USING (SELECT id FROM job_runs WHERE started_at < p_before
             ORDER BY started_at LIMIT least(greatest(p_limit, 1), 50000)) old
     WHERE j.id = old.id;
    GET DIAGNOSTICS deleted = ROW_COUNT;
    RETURN deleted;
  END
  $$;


ALTER FUNCTION public.purge_job_runs(p_before timestamp with time zone, p_limit integer) OWNER TO postgres;

--
-- Name: purge_mfa_challenges(timestamp with time zone, integer); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.purge_mfa_challenges(p_before timestamp with time zone, p_limit integer) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    deleted integer;
  BEGIN
    IF p_before IS NULL OR p_before > now() - interval '1 hour' THEN
      RAISE EXCEPTION 'purge_mfa_challenges: % is inside the 1-hour minimum retention', p_before
        USING ERRCODE = 'check_violation';
    END IF;
    DELETE FROM mfa_challenges c
     USING (SELECT id FROM mfa_challenges WHERE expires_at < p_before
             ORDER BY expires_at LIMIT least(greatest(p_limit, 1), 50000)
             FOR UPDATE SKIP LOCKED) old
     WHERE c.id = old.id AND c.expires_at < p_before;
    GET DIAGNOSTICS deleted = ROW_COUNT;
    RETURN deleted;
  END
  $$;


ALTER FUNCTION public.purge_mfa_challenges(p_before timestamp with time zone, p_limit integer) OWNER TO postgres;

--
-- Name: purge_notifications(timestamp with time zone, integer); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.purge_notifications(p_before timestamp with time zone, p_limit integer) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    deleted integer;
  BEGIN
    IF p_before > now() - interval '30 days' THEN
      RAISE EXCEPTION 'purge_notifications: % is inside the 30-day minimum retention', p_before
        USING ERRCODE = 'check_violation';
    END IF;
    DELETE FROM notifications n
     USING (SELECT id FROM notifications WHERE created_at < p_before
             ORDER BY created_at LIMIT least(greatest(p_limit, 1), 50000)
             FOR UPDATE SKIP LOCKED) old
     WHERE n.id = old.id;
    GET DIAGNOSTICS deleted = ROW_COUNT;
    RETURN deleted;
  END
  $$;


ALTER FUNCTION public.purge_notifications(p_before timestamp with time zone, p_limit integer) OWNER TO postgres;

--
-- Name: purge_rate_limit_counters(integer); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.purge_rate_limit_counters(p_limit integer) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    deleted integer;
  BEGIN
    DELETE FROM rate_limit_counters c
     USING (SELECT bucket, key_hash, window_start FROM rate_limit_counters WHERE expires_at < now()
             ORDER BY expires_at LIMIT least(greatest(p_limit, 1), 50000)) old
     WHERE c.bucket = old.bucket AND c.key_hash = old.key_hash AND c.window_start = old.window_start;
    GET DIAGNOSTICS deleted = ROW_COUNT;
    RETURN deleted;
  END
  $$;


ALTER FUNCTION public.purge_rate_limit_counters(p_limit integer) OWNER TO postgres;

--
-- Name: purge_sessions(timestamp with time zone, integer); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.purge_sessions(p_before timestamp with time zone, p_limit integer) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    deleted integer;
  BEGIN
    IF p_before IS NULL OR p_before > now() - interval '30 days' THEN
      RAISE EXCEPTION 'purge_sessions: % is inside the 30-day minimum retention', p_before
        USING ERRCODE = 'check_violation';
    END IF;
    DELETE FROM sessions s
     USING (SELECT id FROM sessions
             WHERE least(coalesce(revoked_at, 'infinity'::timestamptz), expires_at, absolute_expires_at) < p_before
             ORDER BY least(coalesce(revoked_at, 'infinity'::timestamptz), expires_at, absolute_expires_at)
             LIMIT least(greatest(p_limit, 1), 50000)
             FOR UPDATE SKIP LOCKED) old
     WHERE s.id = old.id
       AND least(coalesce(s.revoked_at, 'infinity'::timestamptz), s.expires_at, s.absolute_expires_at) < p_before;
    GET DIAGNOSTICS deleted = ROW_COUNT;
    RETURN deleted;
  END
  $$;


ALTER FUNCTION public.purge_sessions(p_before timestamp with time zone, p_limit integer) OWNER TO postgres;

--
-- Name: rate_limit_hit(text, text, integer); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.rate_limit_hit(p_bucket text, p_key_hash text, p_window_seconds integer) RETURNS TABLE(hit_count integer, retry_after_seconds integer)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    v_now   timestamptz := clock_timestamp();
    v_start timestamptz;
    v_end   timestamptz;
    v_hits  integer;
  BEGIN
    IF p_window_seconds IS NULL OR p_window_seconds < 1 OR p_window_seconds > 604800 THEN
      RAISE EXCEPTION 'rate_limit_hit: a window is 1 second to 7 days, not %', p_window_seconds
        USING ERRCODE = 'check_violation';
    END IF;
    v_start := to_timestamp(floor(extract(epoch FROM v_now) / p_window_seconds) * p_window_seconds);
    v_end := v_start + make_interval(secs => p_window_seconds);
    INSERT INTO rate_limit_counters AS c (bucket, key_hash, window_start, expires_at, hits)
      VALUES (p_bucket, p_key_hash, v_start, v_end, 1)
      ON CONFLICT (bucket, key_hash, window_start)
      -- Capped so a flood cannot overflow the integer; anything that high is refused anyway.
      DO UPDATE SET hits = least(c.hits, 1000000) + 1
      RETURNING c.hits INTO v_hits;
    hit_count := v_hits;
    retry_after_seconds := greatest(1, ceil(extract(epoch FROM v_end - v_now))::integer);
    RETURN NEXT;
  END
  $$;


ALTER FUNCTION public.rate_limit_hit(p_bucket text, p_key_hash text, p_window_seconds integer) OWNER TO postgres;

--
-- Name: station_bank_accounts_audit_rekey(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.station_bank_accounts_audit_rekey() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
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


ALTER FUNCTION public.station_bank_accounts_audit_rekey() OWNER TO postgres;

--
-- Name: station_bank_accounts_check_insert(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.station_bank_accounts_check_insert() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  BEGIN
    IF NOT EXISTS (SELECT 1 FROM stations WHERE id = NEW.station_id AND operator_id = NEW.operator_id) THEN
      RAISE EXCEPTION 'bank account %: station % is not run by operator %', NEW.id, NEW.station_id, NEW.operator_id
        USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.status <> 'PENDING' THEN
      RAISE EXCEPTION 'bank account % must start as a pending change, not %', NEW.id, NEW.status
        USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.requested_by IS DISTINCT FROM app_user_id() THEN
      RAISE EXCEPTION 'bank account %: the requester must be the acting user', NEW.id
        USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
  END
  $$;


ALTER FUNCTION public.station_bank_accounts_check_insert() OWNER TO postgres;

--
-- Name: station_bank_accounts_guard(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.station_bank_accounts_guard() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public', 'pg_temp'
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


ALTER FUNCTION public.station_bank_accounts_guard() OWNER TO postgres;

--
-- Name: station_finance_same_operator(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.station_finance_same_operator() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  BEGIN
    IF NOT EXISTS (SELECT 1 FROM stations WHERE id = NEW.station_id AND operator_id = NEW.operator_id) THEN
      RAISE EXCEPTION '%: station % is not run by operator %', TG_TABLE_NAME, NEW.station_id, NEW.operator_id
        USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;


ALTER FUNCTION public.station_finance_same_operator() OWNER TO postgres;

--
-- Name: submit_lead(uuid, text, text, text, text, text, text); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.submit_lead(p_id uuid, p_company_name text, p_contact_name text, p_phone text, p_email text, p_fleet_size text, p_locale text) RETURNS TABLE(lead_id uuid, lead_reference text, repeated boolean)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  DECLARE
    v_now       timestamptz := now();
    v_id        uuid;
    v_reference text;
  BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('submit_lead'));
    SELECT l.id, l.reference INTO v_id, v_reference
      FROM leads l
     WHERE (l.phone = p_phone OR l.email = p_email) AND l.last_requested_at > v_now - interval '24 hours'
     ORDER BY l.last_requested_at DESC
     LIMIT 1;
    IF FOUND THEN
      UPDATE leads l
         SET last_requested_at = v_now,
             request_count = l.request_count + 1,
             differing_requests = l.differing_requests
               + CASE WHEN (l.company_name, l.contact_name, l.phone, l.email, l.fleet_size)
                           IS DISTINCT FROM (p_company_name, p_contact_name, p_phone, p_email, p_fleet_size)
                      THEN 1 ELSE 0 END
       WHERE l.id = v_id;
      lead_id := v_id;
      lead_reference := v_reference;
      repeated := true;
      RETURN NEXT;
      RETURN;
    END IF;
    v_reference := 'L-' || nextval('lead_reference_seq');
    INSERT INTO leads (id, reference, company_name, contact_name, phone, email, fleet_size, locale, status, note,
                       request_count, differing_requests, last_requested_at, created_at, updated_at, updated_by,
                       closed_at)
      VALUES (p_id, v_reference, p_company_name, p_contact_name, p_phone, p_email, p_fleet_size, p_locale, 'NEW',
              NULL, 1, 0, v_now, v_now, v_now, NULL, NULL);
    lead_id := p_id;
    lead_reference := v_reference;
    repeated := false;
    RETURN NEXT;
  END
  $$;


ALTER FUNCTION public.submit_lead(p_id uuid, p_company_name text, p_contact_name text, p_phone text, p_email text, p_fleet_size text, p_locale text) OWNER TO postgres;

--
-- Name: tax_id_key(text); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.tax_id_key(p_tax_id text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$
    SELECT regexp_replace(translate(p_tax_id, '٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹', '01234567890123456789'),
                          '[[:space:]\u00A0\u2000-\u200A\u202F\u205F\u3000().\u2010-\u2015-]', '', 'g')
  $$;


ALTER FUNCTION public.tax_id_key(p_tax_id text) OWNER TO postgres;

--
-- Name: FUNCTION tax_id_key(p_tax_id text); Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON FUNCTION public.tax_id_key(p_tax_id text) IS 'The form two tax ids are compared in: digits in ASCII, no separators. The unique indexes and Ops search use it.';


--
-- Name: topup_requests_guard(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.topup_requests_guard() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  BEGIN
    IF TG_OP = 'INSERT' THEN
      IF NEW.legacy_single_step THEN
        RAISE EXCEPTION 'top-up %: a new top-up is never single-step', NEW.id USING ERRCODE = 'check_violation';
      END IF;
      IF NEW.status NOT IN ('PENDING', 'PENDING_APPROVAL') THEN
        RAISE EXCEPTION 'top-up %: a new top-up starts pending', NEW.id USING ERRCODE = 'check_violation';
      END IF;
      IF NEW.verified_by IS NOT NULL AND NEW.verified_by IS DISTINCT FROM app_user_id() THEN
        RAISE EXCEPTION 'top-up %: the verifier must be the acting user', NEW.id USING ERRCODE = 'insufficient_privilege';
      END IF;
      -- V17: a company session writes exactly what TopUps.request writes. 'MAIN' is the column's default
      -- and TopUps.DEFAULT_RECEIVING_ACCOUNT; Operations names the account the money arrived in when it
      -- verifies the request.
      IF app_scope_type() = 'COMPANY' THEN
        IF NEW.origin <> 'COMPANY_REQUEST' THEN
          RAISE EXCEPTION 'top-up %: a company session inserts only a company request', NEW.id
            USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF NEW.verified_amount_piastres IS NOT NULL OR NEW.verified_by IS NOT NULL OR NEW.verified_at IS NOT NULL
           OR NEW.evidence_reference IS NOT NULL THEN
          RAISE EXCEPTION 'top-up %: a request is not verified by the company that makes it', NEW.id
            USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF NEW.receiving_account <> 'MAIN' THEN
          RAISE EXCEPTION 'top-up %: a company request names the default receiving account (MAIN)', NEW.id
            USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.requested_by IS NULL OR NEW.requested_by IS DISTINCT FROM app_user_id() THEN
          RAISE EXCEPTION 'top-up %: the requester must be the acting user', NEW.id
            USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF NEW.decided_by IS NOT NULL OR NEW.decision_reason IS NOT NULL OR NEW.ledger_transaction_id IS NOT NULL THEN
          RAISE EXCEPTION 'top-up %: a new request carries no decision', NEW.id USING ERRCODE = 'check_violation';
        END IF;
      END IF;
      RETURN NEW;
    END IF;
    -- UPDATE: what was claimed, by whom, for whom, and the legacy flag never change; a verification is
    -- written once, by the acting user; a decision is taken by the acting user.
    IF NEW.company_id IS DISTINCT FROM OLD.company_id OR NEW.amount_piastres IS DISTINCT FROM OLD.amount_piastres
       OR NEW.requested_by IS DISTINCT FROM OLD.requested_by OR NEW.requested_at IS DISTINCT FROM OLD.requested_at
       OR NEW.origin IS DISTINCT FROM OLD.origin OR NEW.legacy_single_step IS DISTINCT FROM OLD.legacy_single_step THEN
      RAISE EXCEPTION 'top-up %: its request facts never change', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status NOT IN ('PENDING', 'PENDING_APPROVAL') THEN
      RAISE EXCEPTION 'top-up % is already decided', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    -- The transfer date may be corrected to the statement's only by the verification itself.
    IF NEW.transfer_date IS DISTINCT FROM OLD.transfer_date AND NOT (OLD.status = 'PENDING' AND NEW.status = 'PENDING_APPROVAL') THEN
      RAISE EXCEPTION 'top-up %: the transfer date changes only when it is verified', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.verified_by IS NOT NULL AND (NEW.verified_by IS DISTINCT FROM OLD.verified_by
       OR NEW.transfer_date IS DISTINCT FROM OLD.transfer_date
       OR NEW.verified_amount_piastres IS DISTINCT FROM OLD.verified_amount_piastres
       OR NEW.receiving_account IS DISTINCT FROM OLD.receiving_account
       OR NEW.bank_reference_key IS DISTINCT FROM OLD.bank_reference_key) THEN
      RAISE EXCEPTION 'top-up %: a verification is written once', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.verified_by IS NULL AND NEW.verified_by IS NOT NULL AND NEW.verified_by IS DISTINCT FROM app_user_id() THEN
      RAISE EXCEPTION 'top-up %: the verifier must be the acting user', OLD.id USING ERRCODE = 'insufficient_privilege';
    END IF;
    -- V17: a withdrawal is the company's, of a request nobody has verified. (A company's update policy
    -- already passes only PENDING rows; this holds for every session, the platform and the owner too.)
    IF NEW.status = 'CANCELLED' THEN
      IF OLD.status <> 'PENDING' THEN
        RAISE EXCEPTION 'top-up %: only a request nobody has verified is withdrawn', OLD.id
          USING ERRCODE = 'check_violation';
      END IF;
      IF app_scope_type() IS DISTINCT FROM 'COMPANY' THEN
        RAISE EXCEPTION 'top-up %: only the company withdraws its request; Operations rejects it with a reason', OLD.id
          USING ERRCODE = 'insufficient_privilege';
      END IF;
      -- The company's role has UPDATE on the whole table (V6), so the withdrawal itself must be held to
      -- the status, the decision (decided_by, decided_at, decision_reason) and updated_at. Every other
      -- column stays as it was, and a withdrawn request never points at a posting.
      IF (NEW.id, NEW.bank_reference, NEW.bank_reference_key, NEW.note, NEW.receiving_account,
          NEW.verified_amount_piastres, NEW.verified_by, NEW.verified_at, NEW.evidence_reference,
          NEW.ledger_transaction_id, NEW.created_at, NEW.transfer_date)
         IS DISTINCT FROM
         (OLD.id, OLD.bank_reference, OLD.bank_reference_key, OLD.note, OLD.receiving_account,
          OLD.verified_amount_piastres, OLD.verified_by, OLD.verified_at, OLD.evidence_reference,
          OLD.ledger_transaction_id, OLD.created_at, OLD.transfer_date)
         OR NEW.ledger_transaction_id IS NOT NULL THEN
        RAISE EXCEPTION 'top-up %: a withdrawal changes only the status and the decision', OLD.id
          USING ERRCODE = 'check_violation';
      END IF;
    END IF;
    -- A decision is taken by the acting user, never by nobody (V12 for approvals and rejections; V17
    -- adds withdrawals and the empty decider).
    IF NEW.status IN ('CONFIRMED', 'REJECTED', 'CANCELLED')
       AND (NEW.decided_by IS NULL OR NEW.decided_by IS DISTINCT FROM app_user_id()) THEN
      RAISE EXCEPTION 'top-up %: the decider must be the acting user', OLD.id USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NEW.status = 'CONFIRMED' AND OLD.status <> 'PENDING_APPROVAL' THEN
      RAISE EXCEPTION 'top-up %: only a verified top-up is approved', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;


ALTER FUNCTION public.topup_requests_guard() OWNER TO postgres;

--
-- Name: wallet_adjustments_guard(); Type: FUNCTION; Schema: public; Owner: postgres
--

CREATE FUNCTION public.wallet_adjustments_guard() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public', 'pg_temp'
    AS $$
  BEGIN
    IF TG_OP = 'INSERT' THEN
      IF NEW.status <> 'PENDING_APPROVAL' THEN
        RAISE EXCEPTION 'adjustment %: a new adjustment waits for approval', NEW.id USING ERRCODE = 'check_violation';
      END IF;
      IF NEW.requested_by IS DISTINCT FROM app_user_id() THEN
        RAISE EXCEPTION 'adjustment %: the requester must be the acting user', NEW.id
          USING ERRCODE = 'insufficient_privilege';
      END IF;
      RETURN NEW;
    END IF;
    IF NEW.company_id IS DISTINCT FROM OLD.company_id OR NEW.amount_piastres IS DISTINCT FROM OLD.amount_piastres
       OR NEW.reason IS DISTINCT FROM OLD.reason OR NEW.requested_by IS DISTINCT FROM OLD.requested_by THEN
      RAISE EXCEPTION 'adjustment %: its request never changes', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status <> 'PENDING_APPROVAL' THEN
      RAISE EXCEPTION 'adjustment % is already decided', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.decided_by IS DISTINCT FROM app_user_id() THEN
      RAISE EXCEPTION 'adjustment %: the decider must be the acting user', OLD.id
        USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
  END
  $$;


ALTER FUNCTION public.wallet_adjustments_guard() OWNER TO postgres;

--
-- Name: agent_sessions; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.agent_sessions (
    id uuid NOT NULL,
    agent_id uuid NOT NULL,
    device_id uuid NOT NULL,
    operator_id uuid NOT NULL,
    station_id uuid NOT NULL,
    token_hash text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    last_seen_at timestamp with time zone NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    revoke_reason text
);


ALTER TABLE public.agent_sessions OWNER TO postgres;

--
-- Name: agents; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.agents (
    id uuid NOT NULL,
    operator_id uuid NOT NULL,
    station_id uuid NOT NULL,
    worker_id text NOT NULL,
    full_name text NOT NULL,
    phone text,
    pin_hash text NOT NULL,
    must_change_pin boolean NOT NULL,
    failed_pin_count integer DEFAULT 0 NOT NULL,
    locked_until timestamp with time zone,
    status text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone NOT NULL,
    updated_by uuid,
    CONSTRAINT agents_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'SUSPENDED'::text, 'DEACTIVATED'::text])))
);


ALTER TABLE public.agents OWNER TO postgres;

--
-- Name: alerts; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.alerts (
    id uuid NOT NULL,
    type text NOT NULL,
    severity text NOT NULL,
    status text NOT NULL,
    company_id uuid,
    operator_id uuid,
    station_id uuid,
    vehicle_id uuid,
    fueling_id uuid,
    details jsonb DEFAULT '{}'::jsonb NOT NULL,
    company_visible boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    acknowledged_by uuid,
    acknowledged_at timestamp with time zone,
    device_id uuid,
    CONSTRAINT alerts_acknowledged CHECK (((status = 'ACKNOWLEDGED'::text) = (acknowledged_at IS NOT NULL))),
    CONSTRAINT alerts_severity_check CHECK ((severity = ANY (ARRAY['WARNING'::text, 'CRITICAL'::text]))),
    CONSTRAINT alerts_status_check CHECK ((status = ANY (ARRAY['OPEN'::text, 'ACKNOWLEDGED'::text]))),
    CONSTRAINT alerts_sync_rejected_names_item CHECK (((type <> 'SYNC_REJECTED'::text) OR ((device_id IS NOT NULL) AND (details ? 'clientTxnId'::text) AND (details ? 'code'::text)))),
    CONSTRAINT alerts_type_check CHECK ((type = ANY (ARRAY['TANK_CAPACITY_EXCEEDED'::text, 'VIP_CEILING_PASSED'::text, 'LOW_BALANCE'::text, 'SYNC_CONFLICT'::text, 'SYNC_REJECTED'::text, 'OFFLINE_EXPOSURE'::text, 'OUTCOME_MISMATCH'::text, 'COMPANY_OFFLINE_EXPOSURE'::text])))
);


ALTER TABLE public.alerts OWNER TO postgres;

--
-- Name: audit_log; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.audit_log (
    id uuid NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    actor_id uuid,
    actor_role text,
    action text NOT NULL,
    entity_type text NOT NULL,
    entity_id uuid,
    scope_type text NOT NULL,
    scope_id uuid,
    metadata jsonb DEFAULT '{}'::jsonb NOT NULL,
    request_id text,
    ip_prefix text,
    CONSTRAINT audit_log_scope_id_matches_type CHECK (((scope_type = 'PLATFORM'::text) = (scope_id IS NULL))),
    CONSTRAINT audit_log_scope_type_check CHECK ((scope_type = ANY (ARRAY['PLATFORM'::text, 'COMPANY'::text, 'OPERATOR'::text])))
);


ALTER TABLE public.audit_log OWNER TO postgres;

--
-- Name: TABLE audit_log; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.audit_log IS 'Append only. Written in the same transaction as the change it records. Never updated or deleted.';


--
-- Name: companies; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.companies (
    id uuid NOT NULL,
    legal_name text NOT NULL,
    name_ar text NOT NULL,
    name_en text NOT NULL,
    tax_id text,
    commercial_registration text,
    contact_name text,
    contact_phone text,
    contact_email text,
    status text NOT NULL,
    cooldown_minutes integer NOT NULL,
    low_balance_threshold_piastres bigint NOT NULL,
    created_at timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone NOT NULL,
    updated_by uuid,
    CONSTRAINT companies_cooldown_minutes_check CHECK (((cooldown_minutes >= 120) AND (cooldown_minutes <= 240))),
    CONSTRAINT companies_low_balance_threshold_piastres_check CHECK ((low_balance_threshold_piastres >= 0)),
    CONSTRAINT companies_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'FROZEN'::text])))
);


ALTER TABLE public.companies OWNER TO postgres;

--
-- Name: company_wallets; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.company_wallets (
    company_id uuid NOT NULL,
    account_id uuid NOT NULL,
    balance_piastres bigint DEFAULT 0 NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT company_wallets_balance_piastres_check CHECK ((balance_piastres >= 0))
);


ALTER TABLE public.company_wallets OWNER TO postgres;

--
-- Name: TABLE company_wallets; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.company_wallets IS 'The row the fueling engine locks first (plan 3.4 step 4). The ledger is the truth; this is its cache.';


--
-- Name: data_key_state; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.data_key_state (
    singleton boolean DEFAULT true NOT NULL,
    current_key_id smallint NOT NULL,
    lookup_key_fingerprint text NOT NULL,
    fingerprint_recorded_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT data_key_state_current_key_id_check CHECK (((current_key_id >= 1) AND (current_key_id <= 255))),
    CONSTRAINT data_key_state_lookup_key_fingerprint_check CHECK ((lookup_key_fingerprint ~ '^[0-9a-f]{64}$'::text)),
    CONSTRAINT data_key_state_singleton_check CHECK (singleton)
);


ALTER TABLE public.data_key_state OWNER TO postgres;

--
-- Name: TABLE data_key_state; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.data_key_state IS 'One row: the current data key id and an HMAC fingerprint of the lookup key, written by the owner at every start (DataKeyState, ADR 0027). The runtime role has no grant; it reads the id through data_key_current_id().';


--
-- Name: departments; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.departments (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    name text NOT NULL,
    monthly_budget_piastres bigint,
    status text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone NOT NULL,
    updated_by uuid,
    CONSTRAINT departments_monthly_budget_piastres_check CHECK ((monthly_budget_piastres >= 0)),
    CONSTRAINT departments_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'ARCHIVED'::text])))
);


ALTER TABLE public.departments OWNER TO postgres;

--
-- Name: device_nonces; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.device_nonces (
    device_id uuid NOT NULL,
    nonce text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT device_nonces_nonce_check CHECK (((length(nonce) >= 16) AND (length(nonce) <= 128)))
);


ALTER TABLE public.device_nonces OWNER TO postgres;

--
-- Name: fleet_import_jobs; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.fleet_import_jobs (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    file_name text NOT NULL,
    file_sha256 text NOT NULL,
    file_format text NOT NULL,
    status text NOT NULL,
    mode text,
    row_count integer NOT NULL,
    valid_count integer NOT NULL,
    invalid_count integer NOT NULL,
    imported_count integer DEFAULT 0 NOT NULL,
    existing_count integer DEFAULT 0 NOT NULL,
    failed_count integer DEFAULT 0 NOT NULL,
    created_by uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    confirmed_by uuid,
    confirmed_by_role text,
    confirmed_at timestamp with time zone,
    started_at timestamp with time zone,
    finished_at timestamp with time zone,
    lease_owner text,
    lease_until timestamp with time zone,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT fleet_import_jobs_confirmed CHECK (((status = ANY (ARRAY['VALIDATED'::text, 'CANCELLED'::text])) OR ((confirmed_by IS NOT NULL) AND (confirmed_at IS NOT NULL) AND (mode IS NOT NULL)))),
    CONSTRAINT fleet_import_jobs_counts CHECK (((valid_count + invalid_count) = row_count)),
    CONSTRAINT fleet_import_jobs_existing_count_check CHECK ((existing_count >= 0)),
    CONSTRAINT fleet_import_jobs_failed_count_check CHECK ((failed_count >= 0)),
    CONSTRAINT fleet_import_jobs_file_format_check CHECK ((file_format = ANY (ARRAY['CSV'::text, 'XLSX'::text]))),
    CONSTRAINT fleet_import_jobs_file_name_check CHECK (((length(file_name) >= 1) AND (length(file_name) <= 255))),
    CONSTRAINT fleet_import_jobs_file_sha256_check CHECK ((file_sha256 ~ '^[0-9a-f]{64}$'::text)),
    CONSTRAINT fleet_import_jobs_finished CHECK (((status = ANY (ARRAY['COMPLETED'::text, 'COMPLETED_WITH_ERRORS'::text])) = (finished_at IS NOT NULL))),
    CONSTRAINT fleet_import_jobs_imported_count_check CHECK ((imported_count >= 0)),
    CONSTRAINT fleet_import_jobs_invalid_count_check CHECK ((invalid_count >= 0)),
    CONSTRAINT fleet_import_jobs_mode_check CHECK ((mode = ANY (ARRAY['STRICT'::text, 'VALID_ROWS_ONLY'::text]))),
    CONSTRAINT fleet_import_jobs_row_count_check CHECK (((row_count >= 1) AND (row_count <= 5000))),
    CONSTRAINT fleet_import_jobs_status_check CHECK ((status = ANY (ARRAY['VALIDATED'::text, 'QUEUED'::text, 'RUNNING'::text, 'COMPLETED'::text, 'COMPLETED_WITH_ERRORS'::text, 'CANCELLED'::text]))),
    CONSTRAINT fleet_import_jobs_strict_is_clean CHECK (((mode IS DISTINCT FROM 'STRICT'::text) OR (invalid_count = 0))),
    CONSTRAINT fleet_import_jobs_valid_count_check CHECK ((valid_count >= 0))
);


ALTER TABLE public.fleet_import_jobs OWNER TO postgres;

--
-- Name: TABLE fleet_import_jobs; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.fleet_import_jobs IS 'A bulk vehicle import: one file for one company, validated, confirmed, then run by the API worker.';


--
-- Name: fleet_import_rows; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.fleet_import_rows (
    job_id uuid NOT NULL,
    row_number integer NOT NULL,
    plate text,
    plate_key text,
    fleet_code text,
    data jsonb NOT NULL,
    status text NOT NULL,
    errors jsonb DEFAULT '[]'::jsonb NOT NULL,
    vehicle_id uuid,
    processed_at timestamp with time zone,
    CONSTRAINT fleet_import_rows_imported_has_vehicle CHECK (((status = 'IMPORTED'::text) = (vehicle_id IS NOT NULL))),
    CONSTRAINT fleet_import_rows_row_number_check CHECK ((row_number >= 2)),
    CONSTRAINT fleet_import_rows_status_check CHECK ((status = ANY (ARRAY['VALID'::text, 'INVALID'::text, 'DUPLICATE_IN_FILE'::text, 'EXISTS'::text, 'IMPORTED'::text, 'FAILED'::text, 'SKIPPED'::text])))
);


ALTER TABLE public.fleet_import_rows OWNER TO postgres;

--
-- Name: TABLE fleet_import_rows; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.fleet_import_rows IS 'One row of an import file, normalised, with its validation errors and its outcome.';


--
-- Name: flyway_schema_history; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.flyway_schema_history (
    installed_rank integer NOT NULL,
    version character varying(50),
    description character varying(200) NOT NULL,
    type character varying(20) NOT NULL,
    script character varying(1000) NOT NULL,
    checksum integer,
    installed_by character varying(100) NOT NULL,
    installed_on timestamp without time zone DEFAULT now() NOT NULL,
    execution_time integer NOT NULL,
    success boolean NOT NULL
);


ALTER TABLE public.flyway_schema_history OWNER TO postgres;

--
-- Name: fuel_prices; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.fuel_prices (
    id uuid NOT NULL,
    fuel_type text NOT NULL,
    station_id uuid,
    price_piastres bigint NOT NULL,
    effective_from timestamp with time zone NOT NULL,
    note text,
    created_at timestamp with time zone NOT NULL,
    created_by uuid,
    CONSTRAINT fuel_prices_fuel_type_check CHECK ((fuel_type = ANY (ARRAY['GAS_80'::text, 'GAS_92'::text, 'GAS_95'::text, 'DIESEL'::text, 'CNG'::text]))),
    CONSTRAINT fuel_prices_price_piastres_check CHECK ((price_piastres > 0))
);


ALTER TABLE public.fuel_prices OWNER TO postgres;

--
-- Name: fueling_reference_seq; Type: SEQUENCE; Schema: public; Owner: postgres
--

CREATE SEQUENCE public.fueling_reference_seq
    START WITH 10001
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE public.fueling_reference_seq OWNER TO postgres;

--
-- Name: fueling_reversals; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.fueling_reversals (
    id uuid NOT NULL,
    fueling_id uuid NOT NULL,
    company_id uuid NOT NULL,
    operator_id uuid NOT NULL,
    station_id uuid NOT NULL,
    amount_piastres bigint NOT NULL,
    reason text NOT NULL,
    status text NOT NULL,
    requested_by uuid NOT NULL,
    requested_at timestamp with time zone NOT NULL,
    decided_by uuid,
    decided_at timestamp with time zone,
    decision_reason text,
    ledger_transaction_id uuid,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT fueling_reversals_amount_piastres_check CHECK ((amount_piastres > 0)),
    CONSTRAINT fueling_reversals_approved_has_posting CHECK (((status = 'APPROVED'::text) = (ledger_transaction_id IS NOT NULL))),
    CONSTRAINT fueling_reversals_decided CHECK (((status = 'PENDING'::text) = (decided_at IS NULL))),
    CONSTRAINT fueling_reversals_decider CHECK (((decided_at IS NULL) = (decided_by IS NULL))),
    CONSTRAINT fueling_reversals_reason_check CHECK ((length(btrim(reason)) > 0)),
    CONSTRAINT fueling_reversals_rejection_has_reason CHECK (((status <> 'REJECTED'::text) OR (decision_reason IS NOT NULL))),
    CONSTRAINT fueling_reversals_status_check CHECK ((status = ANY (ARRAY['PENDING'::text, 'APPROVED'::text, 'REJECTED'::text]))),
    CONSTRAINT fueling_reversals_two_person CHECK (((status <> 'APPROVED'::text) OR (decided_by <> requested_by)))
);


ALTER TABLE public.fueling_reversals OWNER TO postgres;

--
-- Name: TABLE fueling_reversals; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.fueling_reversals IS 'A credit note for a completed fueling (PRD 6.4): requested by one person, approved by another, posted as the mirror of the fueling. The fueling row never changes.';


--
-- Name: fuelings; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.fuelings (
    id uuid NOT NULL,
    client_txn_id uuid NOT NULL,
    reference text NOT NULL,
    status text NOT NULL,
    decline_reason text,
    origin text NOT NULL,
    company_id uuid NOT NULL,
    vehicle_id uuid NOT NULL,
    tag_id uuid NOT NULL,
    operator_id uuid NOT NULL,
    station_id uuid NOT NULL,
    device_id uuid NOT NULL,
    agent_id uuid NOT NULL,
    fuel_type text NOT NULL,
    volume_ml bigint NOT NULL,
    unit_price_piastres bigint NOT NULL,
    amount_piastres bigint NOT NULL,
    billable boolean NOT NULL,
    evaluated_at timestamp with time zone NOT NULL,
    device_at timestamp with time zone NOT NULL,
    received_at timestamp with time zone NOT NULL,
    receipt_locale text NOT NULL,
    plate text NOT NULL,
    company_name_ar text NOT NULL,
    company_name_en text NOT NULL,
    station_name_ar text NOT NULL,
    station_name_en text NOT NULL,
    agent_worker_id text NOT NULL,
    ledger_transaction_id uuid,
    conflicts text[] DEFAULT '{}'::text[] NOT NULL,
    payable_held boolean DEFAULT false NOT NULL,
    CONSTRAINT fuelings_amount_piastres_check CHECK ((amount_piastres >= 0)),
    CONSTRAINT fuelings_conflicts_known CHECK ((conflicts <@ ARRAY['COOLDOWN'::text, 'TAG_INACTIVE'::text, 'VEHICLE_INACTIVE'::text, 'COMPANY_FROZEN'::text, 'FUEL_TYPE_MISMATCH'::text, 'FUEL_NOT_AVAILABLE'::text, 'LIMIT_EXCEEDED'::text, 'INSUFFICIENT_BALANCE'::text, 'STALE_CAPTURE'::text, 'AGENT_UNVERIFIED'::text, 'DEVICE_CLOCK_SKEW'::text, 'DECLINED_ONLINE'::text, 'STATION_NOT_ACTIVE'::text, 'PRICE_MISMATCH'::text, 'OVER_OFFLINE_ALLOWANCE'::text])),
    CONSTRAINT fuelings_conflicts_offline_only CHECK ((((origin = 'ONLINE'::text) AND (conflicts = '{}'::text[])) OR ((origin = 'OFFLINE'::text) AND (status = 'COMPLETED'::text)))),
    CONSTRAINT fuelings_decline_reason_check CHECK ((decline_reason = ANY (ARRAY['COOLDOWN'::text, 'TAG_INACTIVE'::text, 'VEHICLE_INACTIVE'::text, 'COMPANY_FROZEN'::text, 'FUEL_TYPE_MISMATCH'::text, 'FUEL_NOT_AVAILABLE'::text, 'LIMIT_EXCEEDED'::text, 'INSUFFICIENT_BALANCE'::text]))),
    CONSTRAINT fuelings_declined_has_reason CHECK (((status = 'DECLINED'::text) = (decline_reason IS NOT NULL))),
    CONSTRAINT fuelings_fuel_type_check CHECK ((fuel_type = ANY (ARRAY['GAS_80'::text, 'GAS_92'::text, 'GAS_95'::text, 'DIESEL'::text, 'CNG'::text]))),
    CONSTRAINT fuelings_held_offline_only CHECK (((NOT payable_held) OR (origin = 'OFFLINE'::text))),
    CONSTRAINT fuelings_origin_check CHECK ((origin = ANY (ARRAY['ONLINE'::text, 'OFFLINE'::text]))),
    CONSTRAINT fuelings_posted_iff_charged CHECK (((ledger_transaction_id IS NOT NULL) = ((status = 'COMPLETED'::text) AND billable AND (amount_piastres > 0)))),
    CONSTRAINT fuelings_receipt_locale_check CHECK ((receipt_locale = ANY (ARRAY['ar'::text, 'en'::text]))),
    CONSTRAINT fuelings_status_check CHECK ((status = ANY (ARRAY['COMPLETED'::text, 'DECLINED'::text]))),
    CONSTRAINT fuelings_unit_price_piastres_check CHECK ((unit_price_piastres >= 0)),
    CONSTRAINT fuelings_volume_ml_check CHECK ((volume_ml > 0))
);


ALTER TABLE public.fuelings OWNER TO postgres;

--
-- Name: idempotency_keys; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.idempotency_keys (
    actor_id uuid NOT NULL,
    operation text NOT NULL,
    key text NOT NULL,
    request_hash text NOT NULL,
    resource_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT idempotency_keys_key_check CHECK (((length(key) >= 8) AND (length(key) <= 200))),
    CONSTRAINT idempotency_keys_operation_check CHECK ((operation ~ '^[a-z][a-z0-9_.]{1,63}$'::text)),
    CONSTRAINT idempotency_keys_request_hash_check CHECK ((request_hash ~ '^[0-9a-f]{64}$'::text))
);


ALTER TABLE public.idempotency_keys OWNER TO postgres;

--
-- Name: TABLE idempotency_keys; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.idempotency_keys IS 'Idempotency-Key per user and operation: the request body hash and the resource it created. Never updated; purged after zawed.idempotency.key-retention (30 days by default, never under 7; V17, ADR 0022).';


--
-- Name: job_runs; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.job_runs (
    id uuid NOT NULL,
    job_name text NOT NULL,
    instance_id text NOT NULL,
    started_at timestamp with time zone NOT NULL,
    finished_at timestamp with time zone NOT NULL,
    outcome text NOT NULL,
    counts jsonb DEFAULT '{}'::jsonb NOT NULL,
    error_class text,
    CONSTRAINT job_runs_counts_check CHECK (public.job_run_counts_valid(counts)),
    CONSTRAINT job_runs_error_class_check CHECK ((error_class ~ '^[A-Za-z_$][A-Za-z0-9_$.]{0,199}$'::text)),
    CONSTRAINT job_runs_error_only_on_failure CHECK (((outcome = 'FAILED'::text) = (error_class IS NOT NULL))),
    CONSTRAINT job_runs_finished_after_start CHECK ((finished_at >= started_at)),
    CONSTRAINT job_runs_instance_id_check CHECK ((instance_id ~ '^[A-Za-z0-9@._:-]{1,128}$'::text)),
    CONSTRAINT job_runs_job_name_check CHECK ((job_name ~ '^[a-z][a-z0-9-]{0,62}$'::text)),
    CONSTRAINT job_runs_outcome_check CHECK ((outcome = ANY (ARRAY['SUCCESS'::text, 'FAILED'::text, 'SKIPPED_LOCKED'::text])))
);


ALTER TABLE public.job_runs OWNER TO postgres;

--
-- Name: TABLE job_runs; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.job_runs IS 'One row per run of a scheduled job: outcome, timings, numeric counts, error class. Append only, written by job_run_record; purged after 90 days (V21, ADR 0028).';


--
-- Name: lead_reference_seq; Type: SEQUENCE; Schema: public; Owner: postgres
--

CREATE SEQUENCE public.lead_reference_seq
    START WITH 10001
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE public.lead_reference_seq OWNER TO postgres;

--
-- Name: leads; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.leads (
    id uuid NOT NULL,
    reference text NOT NULL,
    company_name text NOT NULL,
    contact_name text NOT NULL,
    phone text NOT NULL,
    email text NOT NULL,
    fleet_size text NOT NULL,
    locale text NOT NULL,
    status text DEFAULT 'NEW'::text NOT NULL,
    note text,
    request_count integer DEFAULT 1 NOT NULL,
    differing_requests integer DEFAULT 0 NOT NULL,
    last_requested_at timestamp with time zone NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    updated_by uuid,
    closed_at timestamp with time zone,
    CONSTRAINT leads_check CHECK (((differing_requests >= 0) AND (differing_requests < request_count))),
    CONSTRAINT leads_check1 CHECK (((status = 'CLOSED'::text) = (closed_at IS NOT NULL))),
    CONSTRAINT leads_company_name_check CHECK (((char_length(company_name) >= 2) AND (char_length(company_name) <= 120))),
    CONSTRAINT leads_contact_name_check CHECK (((char_length(contact_name) >= 2) AND (char_length(contact_name) <= 80))),
    CONSTRAINT leads_email_check CHECK ((((char_length(email) >= 3) AND (char_length(email) <= 254)) AND (email ~ '^[^@[:space:]]+@[^@[:space:]]+$'::text))),
    CONSTRAINT leads_fleet_size_check CHECK ((fleet_size = ANY (ARRAY['1-19'::text, '20-49'::text, '50-99'::text, '100-249'::text, '250+'::text]))),
    CONSTRAINT leads_locale_check CHECK ((locale = ANY (ARRAY['ar'::text, 'en'::text]))),
    CONSTRAINT leads_note_check CHECK (((note IS NULL) OR ((char_length(note) >= 1) AND (char_length(note) <= 1000)))),
    CONSTRAINT leads_phone_check CHECK ((phone ~ '^01[0125][0-9]{8}$'::text)),
    CONSTRAINT leads_reference_check CHECK ((reference ~ '^L-[0-9]{5,}$'::text)),
    CONSTRAINT leads_request_count_check CHECK ((request_count >= 1)),
    CONSTRAINT leads_status_check CHECK ((status = ANY (ARRAY['NEW'::text, 'CONTACTED'::text, 'QUALIFIED'::text, 'CLOSED'::text])))
);


ALTER TABLE public.leads OWNER TO postgres;

--
-- Name: TABLE leads; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.leads IS 'Demo requests from the public landing page (U1). Personal data: platform users only; inserted only by submit_lead; closed leads purged after their retention by purge_closed_leads (V22).';


--
-- Name: ledger_accounts; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.ledger_accounts (
    id uuid NOT NULL,
    code text NOT NULL,
    kind text NOT NULL,
    company_id uuid,
    station_id uuid,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT ledger_accounts_kind_check CHECK ((kind = ANY (ARRAY['PLATFORM_BANK'::text, 'PLATFORM_ADJUSTMENT'::text, 'COMPANY_WALLET'::text, 'COMPANY_RECEIVABLE'::text, 'STATION_PAYABLE'::text]))),
    CONSTRAINT ledger_accounts_owner_matches_kind CHECK ((((kind = ANY (ARRAY['PLATFORM_BANK'::text, 'PLATFORM_ADJUSTMENT'::text])) AND (company_id IS NULL) AND (station_id IS NULL)) OR ((kind = ANY (ARRAY['COMPANY_WALLET'::text, 'COMPANY_RECEIVABLE'::text])) AND (company_id IS NOT NULL) AND (station_id IS NULL)) OR ((kind = 'STATION_PAYABLE'::text) AND (company_id IS NULL) AND (station_id IS NOT NULL))))
);


ALTER TABLE public.ledger_accounts OWNER TO postgres;

--
-- Name: TABLE ledger_accounts; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.ledger_accounts IS 'Named accounts of the double-entry ledger (plan 3.5). Amounts live on ledger_entries.';


--
-- Name: COLUMN ledger_accounts.kind; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON COLUMN public.ledger_accounts.kind IS 'PLATFORM_ADJUSTMENT: the counter-account of approved wallet credits that are not bank transfers (V12).';


--
-- Name: ledger_entries; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.ledger_entries (
    id uuid NOT NULL,
    seq bigint NOT NULL,
    transaction_id uuid NOT NULL,
    account_id uuid NOT NULL,
    company_id uuid,
    direction text NOT NULL,
    amount_piastres bigint NOT NULL,
    balance_after_piastres bigint,
    posted_at timestamp with time zone NOT NULL,
    CONSTRAINT ledger_entries_amount_piastres_check CHECK ((amount_piastres > 0)),
    CONSTRAINT ledger_entries_direction_check CHECK ((direction = ANY (ARRAY['DEBIT'::text, 'CREDIT'::text])))
);


ALTER TABLE public.ledger_entries OWNER TO postgres;

--
-- Name: TABLE ledger_entries; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.ledger_entries IS 'One side of a posting, in integer piastres. Append only, for every role.';


--
-- Name: COLUMN ledger_entries.balance_after_piastres; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON COLUMN public.ledger_entries.balance_after_piastres IS 'The cached balance right after this entry: a company wallet (never negative) or a station payable (any sign, V9). NULL on other accounts and on payable entries written before V9.';


--
-- Name: ledger_entries_seq_seq; Type: SEQUENCE; Schema: public; Owner: postgres
--

ALTER TABLE public.ledger_entries ALTER COLUMN seq ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.ledger_entries_seq_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: ledger_transactions; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.ledger_transactions (
    id uuid NOT NULL,
    source_type text NOT NULL,
    source_id uuid NOT NULL,
    company_id uuid,
    reference text,
    description text,
    posted_at timestamp with time zone NOT NULL,
    posted_by uuid,
    reverses_transaction_id uuid,
    CONSTRAINT ledger_transactions_not_its_own_reversal CHECK ((reverses_transaction_id <> id)),
    CONSTRAINT ledger_transactions_reversal_names_original CHECK (((source_type = 'REVERSAL'::text) = (reverses_transaction_id IS NOT NULL))),
    CONSTRAINT ledger_transactions_source_type_check CHECK ((source_type = ANY (ARRAY['TOPUP'::text, 'FUELING'::text, 'PAYOUT'::text, 'REVERSAL'::text, 'ADJUSTMENT'::text])))
);


ALTER TABLE public.ledger_transactions OWNER TO postgres;

--
-- Name: TABLE ledger_transactions; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.ledger_transactions IS 'One balanced posting. Immutable: a correction is a new transaction (REVERSAL or ADJUSTMENT).';


--
-- Name: limit_rules; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.limit_rules (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    vehicle_id uuid NOT NULL,
    kind text NOT NULL,
    period text NOT NULL,
    max_value bigint NOT NULL,
    active boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    created_by uuid,
    deactivated_at timestamp with time zone,
    deactivated_by uuid,
    CONSTRAINT limit_rules_kind_check CHECK ((kind = ANY (ARRAY['VOLUME'::text, 'AMOUNT'::text]))),
    CONSTRAINT limit_rules_max_value_check CHECK ((max_value > 0)),
    CONSTRAINT limit_rules_period_check CHECK ((period = ANY (ARRAY['DAY'::text, 'WEEK'::text, 'MONTH'::text])))
);


ALTER TABLE public.limit_rules OWNER TO postgres;

--
-- Name: mfa_challenges; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.mfa_challenges (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    purpose text NOT NULL,
    door text NOT NULL,
    token_hash text NOT NULL,
    scope_type text NOT NULL,
    scope_id uuid,
    pending_secret_encrypted bytea,
    attempts integer DEFAULT 0 NOT NULL,
    created_at timestamp with time zone NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    used_at timestamp with time zone,
    user_agent text,
    ip_prefix text,
    CONSTRAINT mfa_challenges_door_check CHECK ((door = ANY (ARRAY['USER'::text, 'ADMIN'::text]))),
    CONSTRAINT mfa_challenges_purpose_check CHECK ((purpose = ANY (ARRAY['VERIFY'::text, 'ENROLL'::text]))),
    CONSTRAINT mfa_challenges_scope_type_check CHECK ((scope_type = ANY (ARRAY['PLATFORM'::text, 'COMPANY'::text, 'OPERATOR'::text])))
);


ALTER TABLE public.mfa_challenges OWNER TO postgres;

--
-- Name: TABLE mfa_challenges; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.mfa_challenges IS 'Second sign-in step after a correct password. Single use, short-lived, bound to one user. Purged zawed.sessions.mfa-challenge-retention after expiry (1 day by default, never under 1 hour; V21).';


--
-- Name: notification_read_marks; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.notification_read_marks (
    user_id uuid NOT NULL,
    read_up_to timestamp with time zone NOT NULL,
    marked_at timestamp with time zone NOT NULL
);


ALTER TABLE public.notification_read_marks OWNER TO postgres;

--
-- Name: TABLE notification_read_marks; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.notification_read_marks IS 'Per-user "read up to" mark: every notification created at or before read_up_to is read. Moves forward only.';


--
-- Name: notification_reads; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.notification_reads (
    user_id uuid NOT NULL,
    notification_id uuid NOT NULL,
    read_at timestamp with time zone NOT NULL
);


ALTER TABLE public.notification_reads OWNER TO postgres;

--
-- Name: TABLE notification_reads; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.notification_reads IS 'Per-user read state of notifications: one row per user and notification read. Append only for zawed_app.';


--
-- Name: notifications; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.notifications (
    id uuid NOT NULL,
    type text NOT NULL,
    audience_scope_type text NOT NULL,
    audience_scope_id uuid,
    audience_roles text[] NOT NULL,
    entity_type text NOT NULL,
    entity_id uuid NOT NULL,
    params jsonb DEFAULT '{}'::jsonb NOT NULL,
    actor_id uuid,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT notifications_audience_roles CHECK (((cardinality(audience_roles) >= 1) AND (audience_roles <@
CASE audience_scope_type
    WHEN 'PLATFORM'::text THEN ARRAY['SUPER_ADMIN'::text, 'OPS_MANAGER'::text, 'OPS'::text]
    WHEN 'COMPANY'::text THEN ARRAY['FLEET_MANAGER'::text, 'ACCOUNTANT'::text]
    WHEN 'OPERATOR'::text THEN ARRAY['STATION_MANAGER'::text]
    ELSE NULL::text[]
END))),
    CONSTRAINT notifications_audience_scope CHECK (((audience_scope_type = 'PLATFORM'::text) = (audience_scope_id IS NULL))),
    CONSTRAINT notifications_audience_scope_type_check CHECK ((audience_scope_type = ANY (ARRAY['PLATFORM'::text, 'COMPANY'::text, 'OPERATOR'::text]))),
    CONSTRAINT notifications_entity_type_check CHECK ((entity_type = ANY (ARRAY['COMPANY_WALLET'::text, 'TOPUP'::text, 'WALLET_ADJUSTMENT'::text, 'ALERT'::text, 'TICKET'::text, 'PAYOUT'::text, 'STATION_BANK_ACCOUNT'::text]))),
    CONSTRAINT notifications_params CHECK (public.notification_params_valid(type, params)),
    CONSTRAINT notifications_type_check CHECK ((type = ANY (ARRAY['WALLET_LOW_BALANCE'::text, 'TOPUP_CREDITED'::text, 'TOPUP_REJECTED'::text, 'TICKET_ANSWERED'::text, 'TOPUP_AWAITING_APPROVAL'::text, 'WALLET_ADJUSTMENT_AWAITING_APPROVAL'::text, 'HARD_ALERT_RAISED'::text, 'TICKET_OPENED'::text, 'PAYOUT_POSTED'::text, 'BANK_ACCOUNT_CHANGE_APPROVED'::text, 'BANK_ACCOUNT_CHANGE_REJECTED'::text])))
);


ALTER TABLE public.notifications OWNER TO postgres;

--
-- Name: TABLE notifications; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.notifications IS 'In-app notifications addressed to a scope and roles; ids, integers and codes only. Append only for zawed_app.';


--
-- Name: payable_hold_releases; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.payable_hold_releases (
    id uuid NOT NULL,
    fueling_id uuid NOT NULL,
    station_id uuid NOT NULL,
    operator_id uuid NOT NULL,
    amount_piastres bigint NOT NULL,
    reason text NOT NULL,
    released_by uuid NOT NULL,
    released_at timestamp with time zone NOT NULL,
    CONSTRAINT payable_hold_releases_amount_piastres_check CHECK ((amount_piastres > 0)),
    CONSTRAINT payable_hold_releases_reason_check CHECK ((length(btrim(reason)) > 0))
);


ALTER TABLE public.payable_hold_releases OWNER TO postgres;

--
-- Name: TABLE payable_hold_releases; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.payable_hold_releases IS 'Operations releases a held payable; the fueling row itself is immutable. A released amount counts as available for payout.';


--
-- Name: payouts; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.payouts (
    id uuid NOT NULL,
    station_id uuid NOT NULL,
    operator_id uuid NOT NULL,
    bank_account_id uuid NOT NULL,
    amount_piastres bigint NOT NULL,
    bank_reference text NOT NULL,
    bank_reference_key text NOT NULL,
    value_date date NOT NULL,
    note text,
    status text NOT NULL,
    approval_threshold_piastres bigint NOT NULL,
    approval_required boolean NOT NULL,
    requested_by uuid NOT NULL,
    requested_at timestamp with time zone NOT NULL,
    decided_by uuid,
    decided_at timestamp with time zone,
    decision_reason text,
    posted_at timestamp with time zone,
    ledger_transaction_id uuid,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT payouts_amount_piastres_check CHECK ((amount_piastres > 0)),
    CONSTRAINT payouts_approval_follows_threshold CHECK ((approval_required = (amount_piastres > approval_threshold_piastres))),
    CONSTRAINT payouts_approval_threshold_piastres_check CHECK ((approval_threshold_piastres >= 0)),
    CONSTRAINT payouts_at_once_has_no_approver CHECK (((status <> 'POSTED'::text) OR approval_required OR (decided_by IS NULL))),
    CONSTRAINT payouts_bank_reference_key_check CHECK ((bank_reference_key ~ '^[A-Z0-9]+$'::text)),
    CONSTRAINT payouts_cancelled_by CHECK (((status <> 'CANCELLED'::text) OR ((decided_by IS NOT NULL) AND (decision_reason IS NOT NULL)))),
    CONSTRAINT payouts_decider CHECK (((decided_at IS NULL) = (decided_by IS NULL))),
    CONSTRAINT payouts_pending_awaits_approval CHECK (((status <> 'PENDING'::text) OR (approval_required AND (decided_at IS NULL)))),
    CONSTRAINT payouts_posted_at CHECK (((status = 'POSTED'::text) = (posted_at IS NOT NULL))),
    CONSTRAINT payouts_posted_has_posting CHECK (((status = 'POSTED'::text) = (ledger_transaction_id IS NOT NULL))),
    CONSTRAINT payouts_status_check CHECK ((status = ANY (ARRAY['PENDING'::text, 'POSTED'::text, 'CANCELLED'::text]))),
    CONSTRAINT payouts_two_person CHECK (((status <> 'POSTED'::text) OR (NOT approval_required) OR ((decided_by IS NOT NULL) AND (decided_by <> requested_by))))
);


ALTER TABLE public.payouts OWNER TO postgres;

--
-- Name: TABLE payouts; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.payouts IS 'A bank transfer to a station (A1): station payable to platform:bank. Above the threshold it waits for a second person.';


--
-- Name: pos_devices; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.pos_devices (
    id uuid NOT NULL,
    operator_id uuid NOT NULL,
    station_id uuid NOT NULL,
    code text NOT NULL,
    label text,
    status text NOT NULL,
    activation_code_hash text,
    activation_expires_at timestamp with time zone,
    activation_attempts integer DEFAULT 0 NOT NULL,
    hardware_id text,
    bound_at timestamp with time zone,
    last_sync_at timestamp with time zone,
    revoked_at timestamp with time zone,
    revoked_reason text,
    created_at timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone NOT NULL,
    updated_by uuid,
    public_key bytea,
    key_algorithm text,
    CONSTRAINT pos_devices_active_has_key CHECK (((status <> 'ACTIVE'::text) OR (public_key IS NOT NULL))),
    CONSTRAINT pos_devices_key_algorithm_check CHECK ((key_algorithm = 'EC_P256'::text)),
    CONSTRAINT pos_devices_status_check CHECK ((status = ANY (ARRAY['PENDING_ACTIVATION'::text, 'ACTIVE'::text, 'REVOKED'::text])))
);


ALTER TABLE public.pos_devices OWNER TO postgres;

--
-- Name: rate_limit_counters; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.rate_limit_counters (
    bucket text NOT NULL,
    key_hash text NOT NULL,
    window_start timestamp with time zone NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    hits integer NOT NULL,
    CONSTRAINT rate_limit_counters_bucket_check CHECK ((bucket ~ '^[a-z][a-z0-9-]{1,63}$'::text)),
    CONSTRAINT rate_limit_counters_check CHECK ((expires_at > window_start)),
    CONSTRAINT rate_limit_counters_hits_check CHECK ((hits >= 1)),
    CONSTRAINT rate_limit_counters_key_hash_check CHECK ((key_hash ~ '^[0-9a-f]{64}$'::text))
);


ALTER TABLE public.rate_limit_counters OWNER TO postgres;

--
-- Name: TABLE rate_limit_counters; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.rate_limit_counters IS 'Fixed-window request counters shared by every instance (ADR 0012, V22). Written only by rate_limit_hit, removed only by purge_rate_limit_counters; the runtime role has no grant on the table.';


--
-- Name: reconciliation_findings; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.reconciliation_findings (
    id uuid NOT NULL,
    run_id uuid NOT NULL,
    company_id uuid,
    kind text NOT NULL,
    cached_balance_piastres bigint,
    ledger_balance_piastres bigint,
    difference_piastres bigint,
    created_at timestamp with time zone NOT NULL,
    station_id uuid,
    CONSTRAINT reconciliation_findings_kind_check CHECK ((kind = ANY (ARRAY['BALANCE_MISMATCH'::text, 'MISSING_WALLET'::text, 'PAYABLE_MISMATCH'::text, 'MISSING_PAYABLE'::text]))),
    CONSTRAINT reconciliation_findings_subject CHECK ((((kind = ANY (ARRAY['BALANCE_MISMATCH'::text, 'MISSING_WALLET'::text])) AND (company_id IS NOT NULL) AND (station_id IS NULL)) OR ((kind = ANY (ARRAY['PAYABLE_MISMATCH'::text, 'MISSING_PAYABLE'::text])) AND (company_id IS NULL) AND (station_id IS NOT NULL))))
);


ALTER TABLE public.reconciliation_findings OWNER TO postgres;

--
-- Name: TABLE reconciliation_findings; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.reconciliation_findings IS 'Output of the nightly comparison of every cached wallet balance with its ledger (PRD 4.2, 8.3).';


--
-- Name: rfid_tags; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.rfid_tags (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    vehicle_id uuid NOT NULL,
    uid_encrypted bytea NOT NULL,
    uid_lookup text NOT NULL,
    uid_last4 text NOT NULL,
    status text NOT NULL,
    status_reason text,
    cooldown_until timestamp with time zone,
    last_write_status text,
    created_at timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone NOT NULL,
    updated_by uuid,
    CONSTRAINT rfid_tags_last_write_status_check CHECK ((last_write_status = ANY (ARRAY['OK'::text, 'FAILED'::text]))),
    CONSTRAINT rfid_tags_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'LOST'::text, 'STOLEN'::text, 'DEACTIVATED'::text, 'REPLACEMENT_PENDING'::text])))
);


ALTER TABLE public.rfid_tags OWNER TO postgres;

--
-- Name: sessions; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.sessions (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    door text NOT NULL,
    refresh_token_hash text NOT NULL,
    previous_refresh_token_hash text,
    rotated_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL,
    last_seen_at timestamp with time zone NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    absolute_expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    revoked_reason text,
    user_agent text,
    ip_prefix text,
    CONSTRAINT sessions_door_check CHECK ((door = ANY (ARRAY['USER'::text, 'ADMIN'::text])))
);


ALTER TABLE public.sessions OWNER TO postgres;

--
-- Name: TABLE sessions; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.sessions IS 'One row per web sign-in. Checked on every request. Revoked and expired rows are kept for the record, then purged zawed.sessions.retention after they end (90 days by default, never under 30; V21, ADR 0028). Not agent_sessions: those are kept.';


--
-- Name: station_bank_accounts; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.station_bank_accounts (
    id uuid NOT NULL,
    station_id uuid NOT NULL,
    operator_id uuid NOT NULL,
    account_kind text NOT NULL,
    account_encrypted bytea NOT NULL,
    account_last4 text NOT NULL,
    bank_name text NOT NULL,
    holder_name text NOT NULL,
    bank_letter_reference text NOT NULL,
    status text NOT NULL,
    requested_by uuid NOT NULL,
    requested_at timestamp with time zone NOT NULL,
    decided_by uuid,
    decided_at timestamp with time zone,
    decision_reason text,
    payouts_held_until timestamp with time zone,
    superseded_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT station_bank_accounts_account_kind_check CHECK ((account_kind = ANY (ARRAY['IBAN'::text, 'ACCOUNT_NUMBER'::text]))),
    CONSTRAINT station_bank_accounts_account_last4_check CHECK ((account_last4 ~ '^[A-Z0-9]{4}$'::text)),
    CONSTRAINT station_bank_accounts_approval_starts_hold CHECK (((status = ANY (ARRAY['ACTIVE'::text, 'SUPERSEDED'::text])) = (payouts_held_until IS NOT NULL))),
    CONSTRAINT station_bank_accounts_decided CHECK (((status = 'PENDING'::text) = (decided_at IS NULL))),
    CONSTRAINT station_bank_accounts_decider CHECK (((decided_at IS NULL) = (decided_by IS NULL))),
    CONSTRAINT station_bank_accounts_rejection_has_reason CHECK (((status <> 'REJECTED'::text) OR (decision_reason IS NOT NULL))),
    CONSTRAINT station_bank_accounts_status_check CHECK ((status = ANY (ARRAY['PENDING'::text, 'ACTIVE'::text, 'REJECTED'::text, 'SUPERSEDED'::text]))),
    CONSTRAINT station_bank_accounts_superseded CHECK (((status = 'SUPERSEDED'::text) = (superseded_at IS NOT NULL))),
    CONSTRAINT station_bank_accounts_two_person CHECK (((status <> ALL (ARRAY['ACTIVE'::text, 'SUPERSEDED'::text])) OR (decided_by <> requested_by)))
);


ALTER TABLE public.station_bank_accounts OWNER TO postgres;

--
-- Name: TABLE station_bank_accounts; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.station_bank_accounts IS 'Where a station is paid (A3). A change is requested by one person and approved by another; approval holds payouts.';


--
-- Name: station_operators; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.station_operators (
    id uuid NOT NULL,
    legal_name text NOT NULL,
    name_ar text NOT NULL,
    name_en text NOT NULL,
    tax_id text,
    commercial_registration text,
    contact_name text,
    contact_phone text,
    contact_email text,
    status text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone NOT NULL,
    updated_by uuid,
    CONSTRAINT station_operators_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'SUSPENDED'::text])))
);


ALTER TABLE public.station_operators OWNER TO postgres;

--
-- Name: station_payables; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.station_payables (
    station_id uuid NOT NULL,
    operator_id uuid NOT NULL,
    account_id uuid NOT NULL,
    balance_piastres bigint DEFAULT 0 NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);


ALTER TABLE public.station_payables OWNER TO postgres;

--
-- Name: TABLE station_payables; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.station_payables IS 'The row every posting to a station payable locks (after any wallet). The ledger is the truth; this is its cache.';


--
-- Name: stations; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.stations (
    id uuid NOT NULL,
    operator_id uuid NOT NULL,
    name_ar text NOT NULL,
    name_en text NOT NULL,
    address text,
    latitude numeric(9,6),
    longitude numeric(9,6),
    time_zone text NOT NULL,
    connectivity text NOT NULL,
    fuel_types text[] NOT NULL,
    status text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone NOT NULL,
    updated_by uuid,
    CONSTRAINT stations_connectivity_check CHECK ((connectivity = ANY (ARRAY['NORMAL'::text, 'LOW'::text]))),
    CONSTRAINT stations_fuel_types_check CHECK ((fuel_types <@ ARRAY['GAS_80'::text, 'GAS_92'::text, 'GAS_95'::text, 'DIESEL'::text, 'CNG'::text])),
    CONSTRAINT stations_latitude_check CHECK (((latitude >= ('-90'::integer)::numeric) AND (latitude <= (90)::numeric))),
    CONSTRAINT stations_longitude_check CHECK (((longitude >= ('-180'::integer)::numeric) AND (longitude <= (180)::numeric))),
    CONSTRAINT stations_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'SUSPENDED'::text, 'CLOSED'::text])))
);


ALTER TABLE public.stations OWNER TO postgres;

--
-- Name: sync_batches; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.sync_batches (
    id uuid NOT NULL,
    device_batch_id uuid NOT NULL,
    device_id uuid NOT NULL,
    operator_id uuid NOT NULL,
    station_id uuid NOT NULL,
    received_at timestamp with time zone NOT NULL,
    item_count integer NOT NULL,
    recorded integer NOT NULL,
    duplicates integer NOT NULL,
    rejected integer NOT NULL,
    with_conflicts integer NOT NULL,
    pending_reported integer NOT NULL,
    retried integer DEFAULT 0 NOT NULL,
    CONSTRAINT sync_batches_counts CHECK (((((recorded + duplicates) + rejected) + retried) = item_count)),
    CONSTRAINT sync_batches_duplicates_check CHECK ((duplicates >= 0)),
    CONSTRAINT sync_batches_item_count_check CHECK (((item_count >= 0) AND (item_count <= 100))),
    CONSTRAINT sync_batches_pending_reported_check CHECK ((pending_reported >= 0)),
    CONSTRAINT sync_batches_recorded_check CHECK ((recorded >= 0)),
    CONSTRAINT sync_batches_rejected_check CHECK ((rejected >= 0)),
    CONSTRAINT sync_batches_retried_check CHECK ((retried >= 0)),
    CONSTRAINT sync_batches_with_conflicts_check CHECK ((with_conflicts >= 0))
);


ALTER TABLE public.sync_batches OWNER TO postgres;

--
-- Name: ticket_messages; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.ticket_messages (
    id uuid NOT NULL,
    ticket_id uuid NOT NULL,
    author_id uuid NOT NULL,
    author_role text NOT NULL,
    body text NOT NULL,
    internal boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT ticket_messages_body_check CHECK (((length(body) >= 1) AND (length(body) <= 4000)))
);


ALTER TABLE public.ticket_messages OWNER TO postgres;

--
-- Name: ticket_reference_seq; Type: SEQUENCE; Schema: public; Owner: postgres
--

CREATE SEQUENCE public.ticket_reference_seq
    START WITH 1001
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE public.ticket_reference_seq OWNER TO postgres;

--
-- Name: tickets; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.tickets (
    id uuid NOT NULL,
    reference text NOT NULL,
    scope_type text NOT NULL,
    company_id uuid,
    operator_id uuid,
    type text NOT NULL,
    subject text NOT NULL,
    status text NOT NULL,
    vehicle_id uuid,
    station_id uuid,
    fueling_id uuid,
    created_by uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    assigned_to uuid,
    resolved_at timestamp with time zone,
    CONSTRAINT tickets_resolved CHECK (((status = ANY (ARRAY['RESOLVED'::text, 'CLOSED'::text])) = (resolved_at IS NOT NULL))),
    CONSTRAINT tickets_scope CHECK ((((scope_type = 'COMPANY'::text) AND (company_id IS NOT NULL) AND (operator_id IS NULL)) OR ((scope_type = 'OPERATOR'::text) AND (operator_id IS NOT NULL) AND (company_id IS NULL)))),
    CONSTRAINT tickets_scope_type_check CHECK ((scope_type = ANY (ARRAY['COMPANY'::text, 'OPERATOR'::text]))),
    CONSTRAINT tickets_status_check CHECK ((status = ANY (ARRAY['OPEN'::text, 'IN_PROGRESS'::text, 'RESOLVED'::text, 'CLOSED'::text]))),
    CONSTRAINT tickets_subject_check CHECK (((length(subject) >= 1) AND (length(subject) <= 200))),
    CONSTRAINT tickets_type_check CHECK ((type = ANY (ARRAY['LOST_TAG'::text, 'STOLEN_TAG'::text, 'LIMIT_CHANGE'::text, 'VEHICLE_CHANGE'::text, 'TOPUP_QUERY'::text, 'DEVICE_FAULT'::text, 'TAG_READ_ISSUE'::text, 'PAYOUT_QUERY'::text, 'PRICE_QUERY'::text, 'DISPUTE'::text, 'OTHER'::text])))
);


ALTER TABLE public.tickets OWNER TO postgres;

--
-- Name: topup_requests; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.topup_requests (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    amount_piastres bigint NOT NULL,
    bank_reference text NOT NULL,
    bank_reference_key text NOT NULL,
    transfer_date date NOT NULL,
    note text,
    status text NOT NULL,
    requested_by uuid,
    requested_at timestamp with time zone NOT NULL,
    decided_by uuid,
    decided_at timestamp with time zone,
    decision_reason text,
    ledger_transaction_id uuid,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    origin text DEFAULT 'COMPANY_REQUEST'::text NOT NULL,
    receiving_account text DEFAULT 'MAIN'::text NOT NULL,
    verified_amount_piastres bigint,
    verified_by uuid,
    verified_at timestamp with time zone,
    evidence_reference text,
    legacy_single_step boolean DEFAULT false NOT NULL,
    CONSTRAINT topup_requests_amount_piastres_check CHECK ((amount_piastres > 0)),
    CONSTRAINT topup_requests_bank_reference_key_check CHECK ((bank_reference_key <> ''::text)),
    CONSTRAINT topup_requests_confirmed_has_posting CHECK (((status = 'CONFIRMED'::text) = (ledger_transaction_id IS NOT NULL))),
    CONSTRAINT topup_requests_decided CHECK (((status = ANY (ARRAY['PENDING'::text, 'PENDING_APPROVAL'::text])) = (decided_at IS NULL))),
    CONSTRAINT topup_requests_evidence_reference_check CHECK ((length(evidence_reference) <= 200)),
    CONSTRAINT topup_requests_ops_recorded_is_verified CHECK (((origin <> 'OPS_RECORDED'::text) OR legacy_single_step OR (status = 'CANCELLED'::text) OR (verified_by IS NOT NULL))),
    CONSTRAINT topup_requests_origin_check CHECK ((origin = ANY (ARRAY['COMPANY_REQUEST'::text, 'OPS_RECORDED'::text]))),
    CONSTRAINT topup_requests_receiving_account_check CHECK ((receiving_account ~ '^[A-Z0-9_]{1,32}$'::text)),
    CONSTRAINT topup_requests_reference_key_shape CHECK ((bank_reference_key ~ '^[A-Z0-9]+$'::text)),
    CONSTRAINT topup_requests_rejection_has_reason CHECK (((status <> 'REJECTED'::text) OR (decision_reason IS NOT NULL))),
    CONSTRAINT topup_requests_status_check CHECK ((status = ANY (ARRAY['PENDING'::text, 'PENDING_APPROVAL'::text, 'CONFIRMED'::text, 'REJECTED'::text, 'CANCELLED'::text]))),
    CONSTRAINT topup_requests_two_person CHECK (((status <> 'CONFIRMED'::text) OR legacy_single_step OR ((decided_by IS NOT NULL) AND (decided_by <> verified_by)))),
    CONSTRAINT topup_requests_verified CHECK ((((verified_amount_piastres IS NULL) = (verified_by IS NULL)) AND ((verified_by IS NULL) = (verified_at IS NULL)))),
    CONSTRAINT topup_requests_verified_amount_piastres_check CHECK ((verified_amount_piastres > 0)),
    CONSTRAINT topup_requests_verified_before_approval CHECK (((status <> ALL (ARRAY['PENDING_APPROVAL'::text, 'CONFIRMED'::text])) OR (verified_by IS NOT NULL)))
);


ALTER TABLE public.topup_requests OWNER TO postgres;

--
-- Name: TABLE topup_requests; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.topup_requests IS 'Top-up by bank transfer (PRD 4.6): requested by the company, confirmed or rejected by Operations.';


--
-- Name: COLUMN topup_requests.amount_piastres; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON COLUMN public.topup_requests.amount_piastres IS 'What the company said it transferred (or, for OPS_RECORDED, what Operations recorded). Never what is credited.';


--
-- Name: COLUMN topup_requests.receiving_account; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON COLUMN public.topup_requests.receiving_account IS 'Which ZAWED bank account received the transfer (zawed.finance.receiving-accounts). A reference is unique per account.';


--
-- Name: COLUMN topup_requests.verified_amount_piastres; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON COLUMN public.topup_requests.verified_amount_piastres IS 'What arrived, as Operations read it on the bank statement. The only amount ever credited.';


--
-- Name: users; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.users (
    id uuid NOT NULL,
    email text NOT NULL,
    full_name text NOT NULL,
    password_hash text NOT NULL,
    role text NOT NULL,
    scope_type text NOT NULL,
    scope_id uuid,
    status text NOT NULL,
    must_change_password boolean DEFAULT false NOT NULL,
    failed_login_count integer DEFAULT 0 NOT NULL,
    locked_until timestamp with time zone,
    mfa_secret_encrypted bytea,
    mfa_enabled boolean DEFAULT false NOT NULL,
    mfa_last_used_step bigint,
    password_changed_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone NOT NULL,
    updated_by uuid,
    CONSTRAINT users_failed_login_count_check CHECK ((failed_login_count >= 0)),
    CONSTRAINT users_mfa_secret_present CHECK (((NOT mfa_enabled) OR (mfa_secret_encrypted IS NOT NULL))),
    CONSTRAINT users_role_check CHECK ((role = ANY (ARRAY['SUPER_ADMIN'::text, 'OPS_MANAGER'::text, 'OPS'::text, 'FLEET_MANAGER'::text, 'ACCOUNTANT'::text, 'STATION_MANAGER'::text]))),
    CONSTRAINT users_role_matches_scope CHECK ((((role = ANY (ARRAY['SUPER_ADMIN'::text, 'OPS_MANAGER'::text, 'OPS'::text])) AND (scope_type = 'PLATFORM'::text)) OR ((role = ANY (ARRAY['FLEET_MANAGER'::text, 'ACCOUNTANT'::text])) AND (scope_type = 'COMPANY'::text)) OR ((role = 'STATION_MANAGER'::text) AND (scope_type = 'OPERATOR'::text)))),
    CONSTRAINT users_scope_id_matches_type CHECK (((scope_type = 'PLATFORM'::text) = (scope_id IS NULL))),
    CONSTRAINT users_scope_type_check CHECK ((scope_type = ANY (ARRAY['PLATFORM'::text, 'COMPANY'::text, 'OPERATOR'::text]))),
    CONSTRAINT users_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'SUSPENDED'::text, 'DEACTIVATED'::text])))
);


ALTER TABLE public.users OWNER TO postgres;

--
-- Name: TABLE users; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.users IS 'Web portal users (PRD 3). Never deleted: status DEACTIVATED instead.';


--
-- Name: COLUMN users.mfa_secret_encrypted; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON COLUMN public.users.mfa_secret_encrypted IS 'TOTP secret, AES-256-GCM with a key id byte (platform.crypto.DataEncryptor).';


--
-- Name: COLUMN users.mfa_last_used_step; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON COLUMN public.users.mfa_last_used_step IS 'Highest TOTP time step accepted; a step is never accepted twice.';


--
-- Name: vehicles; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.vehicles (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    department_id uuid,
    plate text NOT NULL,
    plate_key text NOT NULL,
    make text NOT NULL,
    model text NOT NULL,
    year integer,
    fuel_type text NOT NULL,
    tank_capacity_ml bigint NOT NULL,
    tier text NOT NULL,
    vip_monthly_ceiling_ml bigint,
    status text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone NOT NULL,
    updated_by uuid,
    fleet_code text,
    CONSTRAINT vehicles_fleet_code_check CHECK ((fleet_code ~ '^[^[:cntrl:]]{1,40}$'::text)),
    CONSTRAINT vehicles_fuel_type_check CHECK ((fuel_type = ANY (ARRAY['GAS_80'::text, 'GAS_92'::text, 'GAS_95'::text, 'DIESEL'::text, 'CNG'::text]))),
    CONSTRAINT vehicles_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'SUSPENDED'::text, 'DECOMMISSIONED'::text]))),
    CONSTRAINT vehicles_tank_capacity_ceiling CHECK ((tank_capacity_ml <= 1500000)),
    CONSTRAINT vehicles_tank_capacity_ml_check CHECK ((tank_capacity_ml > 0)),
    CONSTRAINT vehicles_tier_check CHECK ((tier = ANY (ARRAY['NORMAL'::text, 'VIP'::text]))),
    CONSTRAINT vehicles_vip_ceiling_only_for_vip CHECK (((tier = 'VIP'::text) OR (vip_monthly_ceiling_ml IS NULL))),
    CONSTRAINT vehicles_vip_monthly_ceiling_ml_check CHECK ((vip_monthly_ceiling_ml > 0)),
    CONSTRAINT vehicles_year_check CHECK (((year >= 1950) AND (year <= 2100)))
);


ALTER TABLE public.vehicles OWNER TO postgres;

--
-- Name: COLUMN vehicles.fleet_code; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON COLUMN public.vehicles.fleet_code IS 'The company''s own number for the vehicle (optional). Unique per company among vehicles in service.';


--
-- Name: wallet_adjustments; Type: TABLE; Schema: public; Owner: postgres
--

CREATE TABLE public.wallet_adjustments (
    id uuid NOT NULL,
    company_id uuid NOT NULL,
    amount_piastres bigint NOT NULL,
    reason text NOT NULL,
    status text NOT NULL,
    requested_by uuid NOT NULL,
    requested_at timestamp with time zone NOT NULL,
    decided_by uuid,
    decided_at timestamp with time zone,
    decision_reason text,
    ledger_transaction_id uuid,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT wallet_adjustments_amount_piastres_check CHECK ((amount_piastres > 0)),
    CONSTRAINT wallet_adjustments_approved_has_posting CHECK (((status = 'APPROVED'::text) = (ledger_transaction_id IS NOT NULL))),
    CONSTRAINT wallet_adjustments_cancelled_by_requester CHECK (((status <> 'CANCELLED'::text) OR (decided_by = requested_by))),
    CONSTRAINT wallet_adjustments_decided CHECK (((status = 'PENDING_APPROVAL'::text) = (decided_at IS NULL))),
    CONSTRAINT wallet_adjustments_decider CHECK (((decided_at IS NULL) = (decided_by IS NULL))),
    CONSTRAINT wallet_adjustments_reason_check CHECK (((length(btrim(reason)) >= 1) AND (length(btrim(reason)) <= 500))),
    CONSTRAINT wallet_adjustments_rejection_has_reason CHECK (((status <> 'REJECTED'::text) OR (decision_reason IS NOT NULL))),
    CONSTRAINT wallet_adjustments_status_check CHECK ((status = ANY (ARRAY['PENDING_APPROVAL'::text, 'APPROVED'::text, 'REJECTED'::text, 'CANCELLED'::text]))),
    CONSTRAINT wallet_adjustments_two_person CHECK (((status <> 'APPROVED'::text) OR (decided_by <> requested_by)))
);


ALTER TABLE public.wallet_adjustments OWNER TO postgres;

--
-- Name: TABLE wallet_adjustments; Type: COMMENT; Schema: public; Owner: postgres
--

COMMENT ON TABLE public.wallet_adjustments IS 'A wallet credit that is not a bank transfer (goodwill, correction). One person asks with a reason, a different person approves; approval posts platform:adjustments to the wallet.';


--
-- Name: agent_sessions agent_sessions_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.agent_sessions
    ADD CONSTRAINT agent_sessions_pkey PRIMARY KEY (id);


--
-- Name: agents agents_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.agents
    ADD CONSTRAINT agents_pkey PRIMARY KEY (id);


--
-- Name: alerts alerts_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.alerts
    ADD CONSTRAINT alerts_pkey PRIMARY KEY (id);


--
-- Name: audit_log audit_log_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.audit_log
    ADD CONSTRAINT audit_log_pkey PRIMARY KEY (id);


--
-- Name: companies companies_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.companies
    ADD CONSTRAINT companies_pkey PRIMARY KEY (id);


--
-- Name: company_wallets company_wallets_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.company_wallets
    ADD CONSTRAINT company_wallets_pkey PRIMARY KEY (company_id);


--
-- Name: data_key_state data_key_state_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.data_key_state
    ADD CONSTRAINT data_key_state_pkey PRIMARY KEY (singleton);


--
-- Name: departments departments_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.departments
    ADD CONSTRAINT departments_pkey PRIMARY KEY (id);


--
-- Name: device_nonces device_nonces_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.device_nonces
    ADD CONSTRAINT device_nonces_pkey PRIMARY KEY (device_id, nonce);


--
-- Name: fleet_import_jobs fleet_import_jobs_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fleet_import_jobs
    ADD CONSTRAINT fleet_import_jobs_pkey PRIMARY KEY (id);


--
-- Name: fleet_import_rows fleet_import_rows_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fleet_import_rows
    ADD CONSTRAINT fleet_import_rows_pkey PRIMARY KEY (job_id, row_number);


--
-- Name: flyway_schema_history flyway_schema_history_pk; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.flyway_schema_history
    ADD CONSTRAINT flyway_schema_history_pk PRIMARY KEY (installed_rank);


--
-- Name: fuel_prices fuel_prices_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fuel_prices
    ADD CONSTRAINT fuel_prices_pkey PRIMARY KEY (id);


--
-- Name: fueling_reversals fueling_reversals_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fueling_reversals
    ADD CONSTRAINT fueling_reversals_pkey PRIMARY KEY (id);


--
-- Name: fuelings fuelings_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fuelings
    ADD CONSTRAINT fuelings_pkey PRIMARY KEY (id);


--
-- Name: idempotency_keys idempotency_keys_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.idempotency_keys
    ADD CONSTRAINT idempotency_keys_pkey PRIMARY KEY (actor_id, operation, key);


--
-- Name: job_runs job_runs_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.job_runs
    ADD CONSTRAINT job_runs_pkey PRIMARY KEY (id);


--
-- Name: leads leads_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.leads
    ADD CONSTRAINT leads_pkey PRIMARY KEY (id);


--
-- Name: leads leads_reference_key; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.leads
    ADD CONSTRAINT leads_reference_key UNIQUE (reference);


--
-- Name: ledger_accounts ledger_accounts_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ledger_accounts
    ADD CONSTRAINT ledger_accounts_pkey PRIMARY KEY (id);


--
-- Name: ledger_entries ledger_entries_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ledger_entries
    ADD CONSTRAINT ledger_entries_pkey PRIMARY KEY (id);


--
-- Name: ledger_transactions ledger_transactions_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ledger_transactions
    ADD CONSTRAINT ledger_transactions_pkey PRIMARY KEY (id);


--
-- Name: limit_rules limit_rules_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.limit_rules
    ADD CONSTRAINT limit_rules_pkey PRIMARY KEY (id);


--
-- Name: mfa_challenges mfa_challenges_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.mfa_challenges
    ADD CONSTRAINT mfa_challenges_pkey PRIMARY KEY (id);


--
-- Name: notification_read_marks notification_read_marks_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.notification_read_marks
    ADD CONSTRAINT notification_read_marks_pkey PRIMARY KEY (user_id);


--
-- Name: notification_reads notification_reads_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.notification_reads
    ADD CONSTRAINT notification_reads_pkey PRIMARY KEY (user_id, notification_id);


--
-- Name: notifications notifications_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.notifications
    ADD CONSTRAINT notifications_pkey PRIMARY KEY (id);


--
-- Name: payable_hold_releases payable_hold_releases_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.payable_hold_releases
    ADD CONSTRAINT payable_hold_releases_pkey PRIMARY KEY (id);


--
-- Name: payouts payouts_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.payouts
    ADD CONSTRAINT payouts_pkey PRIMARY KEY (id);


--
-- Name: pos_devices pos_devices_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.pos_devices
    ADD CONSTRAINT pos_devices_pkey PRIMARY KEY (id);


--
-- Name: rate_limit_counters rate_limit_counters_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.rate_limit_counters
    ADD CONSTRAINT rate_limit_counters_pkey PRIMARY KEY (bucket, key_hash, window_start);


--
-- Name: reconciliation_findings reconciliation_findings_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.reconciliation_findings
    ADD CONSTRAINT reconciliation_findings_pkey PRIMARY KEY (id);


--
-- Name: reconciliation_runs reconciliation_runs_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.reconciliation_runs
    ADD CONSTRAINT reconciliation_runs_pkey PRIMARY KEY (id);


--
-- Name: rfid_tags rfid_tags_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.rfid_tags
    ADD CONSTRAINT rfid_tags_pkey PRIMARY KEY (id);


--
-- Name: sessions sessions_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.sessions
    ADD CONSTRAINT sessions_pkey PRIMARY KEY (id);


--
-- Name: station_bank_accounts station_bank_accounts_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.station_bank_accounts
    ADD CONSTRAINT station_bank_accounts_pkey PRIMARY KEY (id);


--
-- Name: station_operators station_operators_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.station_operators
    ADD CONSTRAINT station_operators_pkey PRIMARY KEY (id);


--
-- Name: station_payables station_payables_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.station_payables
    ADD CONSTRAINT station_payables_pkey PRIMARY KEY (station_id);


--
-- Name: stations stations_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.stations
    ADD CONSTRAINT stations_pkey PRIMARY KEY (id);


--
-- Name: sync_batches sync_batches_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.sync_batches
    ADD CONSTRAINT sync_batches_pkey PRIMARY KEY (id);


--
-- Name: ticket_messages ticket_messages_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ticket_messages
    ADD CONSTRAINT ticket_messages_pkey PRIMARY KEY (id);


--
-- Name: tickets tickets_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.tickets
    ADD CONSTRAINT tickets_pkey PRIMARY KEY (id);


--
-- Name: topup_requests topup_requests_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.topup_requests
    ADD CONSTRAINT topup_requests_pkey PRIMARY KEY (id);


--
-- Name: users users_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_pkey PRIMARY KEY (id);


--
-- Name: vehicles vehicles_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.vehicles
    ADD CONSTRAINT vehicles_pkey PRIMARY KEY (id);


--
-- Name: wallet_adjustments wallet_adjustments_pkey; Type: CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.wallet_adjustments
    ADD CONSTRAINT wallet_adjustments_pkey PRIMARY KEY (id);


--
-- Name: agent_sessions_device_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX agent_sessions_device_id_idx ON public.agent_sessions USING btree (device_id);


--
-- Name: agent_sessions_one_live_per_agent; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX agent_sessions_one_live_per_agent ON public.agent_sessions USING btree (agent_id) WHERE (revoked_at IS NULL);


--
-- Name: agent_sessions_token_hash_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX agent_sessions_token_hash_key ON public.agent_sessions USING btree (token_hash);


--
-- Name: agents_operator_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX agents_operator_id_idx ON public.agents USING btree (operator_id);


--
-- Name: agents_station_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX agents_station_id_idx ON public.agents USING btree (station_id, id);


--
-- Name: agents_worker_id_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX agents_worker_id_key ON public.agents USING btree (worker_id);


--
-- Name: alerts_company_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX alerts_company_id_idx ON public.alerts USING btree (company_id, id);


--
-- Name: alerts_outcome_mismatch_once; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX alerts_outcome_mismatch_once ON public.alerts USING btree (fueling_id) WHERE (type = 'OUTCOME_MISMATCH'::text);


--
-- Name: alerts_status_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX alerts_status_idx ON public.alerts USING btree (status, id);


--
-- Name: alerts_sync_rejected_once; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX alerts_sync_rejected_once ON public.alerts USING btree (device_id, ((details ->> 'clientTxnId'::text)), ((details ->> 'code'::text))) WHERE (type = 'SYNC_REJECTED'::text);


--
-- Name: audit_log_actor_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX audit_log_actor_idx ON public.audit_log USING btree (actor_id, id);


--
-- Name: audit_log_entity_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX audit_log_entity_idx ON public.audit_log USING btree (entity_type, entity_id, id);


--
-- Name: audit_log_occurred_at_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX audit_log_occurred_at_idx ON public.audit_log USING btree (occurred_at);


--
-- Name: audit_log_scope_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX audit_log_scope_idx ON public.audit_log USING btree (scope_type, scope_id, id);


--
-- Name: companies_status_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX companies_status_id_idx ON public.companies USING btree (status, id);


--
-- Name: companies_tax_id_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX companies_tax_id_key ON public.companies USING btree (public.tax_id_key(tax_id)) WHERE (tax_id IS NOT NULL);


--
-- Name: company_wallets_account_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX company_wallets_account_key ON public.company_wallets USING btree (account_id);


--
-- Name: departments_company_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX departments_company_id_idx ON public.departments USING btree (company_id, id);


--
-- Name: departments_name_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX departments_name_key ON public.departments USING btree (company_id, lower(name)) WHERE (status = 'ACTIVE'::text);


--
-- Name: device_nonces_created_at_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX device_nonces_created_at_idx ON public.device_nonces USING btree (created_at);


--
-- Name: fleet_import_jobs_company_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fleet_import_jobs_company_idx ON public.fleet_import_jobs USING btree (company_id, id);


--
-- Name: fleet_import_jobs_one_active; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX fleet_import_jobs_one_active ON public.fleet_import_jobs USING btree (company_id) WHERE (status = ANY (ARRAY['QUEUED'::text, 'RUNNING'::text]));


--
-- Name: fleet_import_jobs_status_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fleet_import_jobs_status_idx ON public.fleet_import_jobs USING btree (status, id);


--
-- Name: fleet_import_rows_status_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fleet_import_rows_status_idx ON public.fleet_import_rows USING btree (job_id, status, row_number);


--
-- Name: fleet_import_rows_vehicle_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fleet_import_rows_vehicle_idx ON public.fleet_import_rows USING btree (vehicle_id) WHERE (vehicle_id IS NOT NULL);


--
-- Name: flyway_schema_history_s_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX flyway_schema_history_s_idx ON public.flyway_schema_history USING btree (success);


--
-- Name: fuel_prices_lookup_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuel_prices_lookup_idx ON public.fuel_prices USING btree (fuel_type, station_id, effective_from DESC);


--
-- Name: fuel_prices_one_per_instant; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX fuel_prices_one_per_instant ON public.fuel_prices USING btree (fuel_type, COALESCE(station_id, '00000000-0000-0000-0000-000000000000'::uuid), effective_from);


--
-- Name: fuel_prices_station_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuel_prices_station_id_idx ON public.fuel_prices USING btree (station_id);


--
-- Name: fueling_reversals_company_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fueling_reversals_company_id_idx ON public.fueling_reversals USING btree (company_id, id);


--
-- Name: fueling_reversals_fueling_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fueling_reversals_fueling_id_idx ON public.fueling_reversals USING btree (fueling_id);


--
-- Name: fueling_reversals_ledger_transaction_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX fueling_reversals_ledger_transaction_key ON public.fueling_reversals USING btree (ledger_transaction_id) WHERE (ledger_transaction_id IS NOT NULL);


--
-- Name: fueling_reversals_one_live; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX fueling_reversals_one_live ON public.fueling_reversals USING btree (fueling_id) WHERE (status = ANY (ARRAY['PENDING'::text, 'APPROVED'::text]));


--
-- Name: fueling_reversals_operator_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fueling_reversals_operator_id_idx ON public.fueling_reversals USING btree (operator_id, id);


--
-- Name: fueling_reversals_station_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fueling_reversals_station_id_idx ON public.fueling_reversals USING btree (station_id, id);


--
-- Name: fueling_reversals_status_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fueling_reversals_status_idx ON public.fueling_reversals USING btree (status, id);


--
-- Name: fuelings_client_txn_id_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX fuelings_client_txn_id_key ON public.fuelings USING btree (client_txn_id);


--
-- Name: fuelings_company_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuelings_company_id_idx ON public.fuelings USING btree (company_id, id);


--
-- Name: fuelings_company_offline_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuelings_company_offline_idx ON public.fuelings USING btree (company_id, evaluated_at) WHERE (origin = 'OFFLINE'::text);


--
-- Name: fuelings_company_time_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuelings_company_time_idx ON public.fuelings USING btree (company_id, evaluated_at);


--
-- Name: fuelings_evaluated_at_brin; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuelings_evaluated_at_brin ON public.fuelings USING brin (evaluated_at);


--
-- Name: fuelings_ledger_transaction_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX fuelings_ledger_transaction_key ON public.fuelings USING btree (ledger_transaction_id) WHERE (ledger_transaction_id IS NOT NULL);


--
-- Name: fuelings_offline_conflicts_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuelings_offline_conflicts_idx ON public.fuelings USING btree (company_id, id) WHERE (conflicts <> '{}'::text[]);


--
-- Name: fuelings_offline_exposure_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuelings_offline_exposure_idx ON public.fuelings USING btree (device_id, company_id, evaluated_at) WHERE (origin = 'OFFLINE'::text);


--
-- Name: fuelings_operator_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuelings_operator_id_idx ON public.fuelings USING btree (operator_id, id);


--
-- Name: fuelings_operator_time_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuelings_operator_time_idx ON public.fuelings USING btree (operator_id, evaluated_at);


--
-- Name: fuelings_payable_held_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuelings_payable_held_idx ON public.fuelings USING btree (station_id, id) WHERE payable_held;


--
-- Name: fuelings_reference_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX fuelings_reference_key ON public.fuelings USING btree (reference);


--
-- Name: fuelings_station_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuelings_station_id_idx ON public.fuelings USING btree (station_id, id);


--
-- Name: fuelings_vehicle_usage_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX fuelings_vehicle_usage_idx ON public.fuelings USING btree (vehicle_id, evaluated_at) WHERE (status = 'COMPLETED'::text);


--
-- Name: idempotency_keys_created_at_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX idempotency_keys_created_at_idx ON public.idempotency_keys USING btree (created_at);


--
-- Name: job_runs_job_outcome_finished_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX job_runs_job_outcome_finished_idx ON public.job_runs USING btree (job_name, outcome, finished_at DESC);


--
-- Name: job_runs_job_started_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX job_runs_job_started_idx ON public.job_runs USING btree (job_name, started_at DESC, id DESC);


--
-- Name: job_runs_started_at_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX job_runs_started_at_idx ON public.job_runs USING btree (started_at);


--
-- Name: leads_closed_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX leads_closed_idx ON public.leads USING btree (closed_at) WHERE (status = 'CLOSED'::text);


--
-- Name: leads_created_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX leads_created_idx ON public.leads USING btree (created_at DESC, id DESC);


--
-- Name: leads_email_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX leads_email_idx ON public.leads USING btree (email, last_requested_at DESC);


--
-- Name: leads_phone_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX leads_phone_idx ON public.leads USING btree (phone, last_requested_at DESC);


--
-- Name: leads_status_created_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX leads_status_created_idx ON public.leads USING btree (status, created_at DESC, id DESC);


--
-- Name: ledger_accounts_code_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX ledger_accounts_code_key ON public.ledger_accounts USING btree (code);


--
-- Name: ledger_accounts_company_kind_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX ledger_accounts_company_kind_key ON public.ledger_accounts USING btree (company_id, kind) WHERE (company_id IS NOT NULL);


--
-- Name: ledger_accounts_station_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX ledger_accounts_station_key ON public.ledger_accounts USING btree (station_id) WHERE (station_id IS NOT NULL);


--
-- Name: ledger_entries_account_seq_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX ledger_entries_account_seq_idx ON public.ledger_entries USING btree (account_id, seq DESC);


--
-- Name: ledger_entries_company_seq_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX ledger_entries_company_seq_idx ON public.ledger_entries USING btree (company_id, seq DESC);


--
-- Name: ledger_entries_seq_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX ledger_entries_seq_key ON public.ledger_entries USING btree (seq);


--
-- Name: ledger_entries_transaction_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX ledger_entries_transaction_id_idx ON public.ledger_entries USING btree (transaction_id);


--
-- Name: ledger_transactions_company_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX ledger_transactions_company_id_idx ON public.ledger_transactions USING btree (company_id, id);


--
-- Name: ledger_transactions_reversed_once; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX ledger_transactions_reversed_once ON public.ledger_transactions USING btree (reverses_transaction_id) WHERE (reverses_transaction_id IS NOT NULL);


--
-- Name: ledger_transactions_source_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX ledger_transactions_source_key ON public.ledger_transactions USING btree (source_type, source_id);


--
-- Name: limit_rules_company_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX limit_rules_company_id_idx ON public.limit_rules USING btree (company_id);


--
-- Name: limit_rules_one_active; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX limit_rules_one_active ON public.limit_rules USING btree (vehicle_id, kind, period) WHERE active;


--
-- Name: limit_rules_vehicle_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX limit_rules_vehicle_id_idx ON public.limit_rules USING btree (vehicle_id);


--
-- Name: mfa_challenges_expires_at_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX mfa_challenges_expires_at_idx ON public.mfa_challenges USING btree (expires_at);


--
-- Name: mfa_challenges_token_hash_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX mfa_challenges_token_hash_key ON public.mfa_challenges USING btree (token_hash);


--
-- Name: mfa_challenges_user_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX mfa_challenges_user_id_idx ON public.mfa_challenges USING btree (user_id);


--
-- Name: notification_reads_notification_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX notification_reads_notification_idx ON public.notification_reads USING btree (notification_id);


--
-- Name: notifications_audience_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX notifications_audience_idx ON public.notifications USING btree (audience_scope_type, audience_scope_id, created_at, id);


--
-- Name: notifications_created_at_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX notifications_created_at_idx ON public.notifications USING btree (created_at);


--
-- Name: payable_hold_releases_fueling_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX payable_hold_releases_fueling_key ON public.payable_hold_releases USING btree (fueling_id);


--
-- Name: payable_hold_releases_operator_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX payable_hold_releases_operator_id_idx ON public.payable_hold_releases USING btree (operator_id, id);


--
-- Name: payable_hold_releases_station_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX payable_hold_releases_station_id_idx ON public.payable_hold_releases USING btree (station_id, id);


--
-- Name: payouts_bank_account_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX payouts_bank_account_id_idx ON public.payouts USING btree (bank_account_id);


--
-- Name: payouts_ledger_transaction_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX payouts_ledger_transaction_key ON public.payouts USING btree (ledger_transaction_id) WHERE (ledger_transaction_id IS NOT NULL);


--
-- Name: payouts_operator_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX payouts_operator_id_idx ON public.payouts USING btree (operator_id, id);


--
-- Name: payouts_pending_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX payouts_pending_idx ON public.payouts USING btree (station_id) WHERE (status = 'PENDING'::text);


--
-- Name: payouts_reference_live_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX payouts_reference_live_key ON public.payouts USING btree (bank_reference_key) WHERE (status = ANY (ARRAY['PENDING'::text, 'POSTED'::text]));


--
-- Name: payouts_station_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX payouts_station_id_idx ON public.payouts USING btree (station_id, id);


--
-- Name: payouts_status_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX payouts_status_idx ON public.payouts USING btree (status, id);


--
-- Name: pos_devices_code_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX pos_devices_code_key ON public.pos_devices USING btree (code);


--
-- Name: pos_devices_operator_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX pos_devices_operator_id_idx ON public.pos_devices USING btree (operator_id);


--
-- Name: pos_devices_station_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX pos_devices_station_id_idx ON public.pos_devices USING btree (station_id, id);


--
-- Name: rate_limit_counters_expires_at_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX rate_limit_counters_expires_at_idx ON public.rate_limit_counters USING btree (expires_at);


--
-- Name: reconciliation_findings_company_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX reconciliation_findings_company_id_idx ON public.reconciliation_findings USING btree (company_id);


--
-- Name: reconciliation_findings_run_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX reconciliation_findings_run_id_idx ON public.reconciliation_findings USING btree (run_id, id);


--
-- Name: reconciliation_findings_station_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX reconciliation_findings_station_id_idx ON public.reconciliation_findings USING btree (station_id);


--
-- Name: reconciliation_runs_started_at_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX reconciliation_runs_started_at_idx ON public.reconciliation_runs USING btree (started_at);


--
-- Name: rfid_tags_company_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX rfid_tags_company_id_idx ON public.rfid_tags USING btree (company_id);


--
-- Name: rfid_tags_one_active_per_vehicle; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX rfid_tags_one_active_per_vehicle ON public.rfid_tags USING btree (vehicle_id) WHERE (status = 'ACTIVE'::text);


--
-- Name: rfid_tags_uid_lookup_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX rfid_tags_uid_lookup_key ON public.rfid_tags USING btree (uid_lookup);


--
-- Name: rfid_tags_vehicle_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX rfid_tags_vehicle_id_idx ON public.rfid_tags USING btree (vehicle_id, id);


--
-- Name: sessions_ended_at_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX sessions_ended_at_idx ON public.sessions USING btree (LEAST(COALESCE(revoked_at, 'infinity'::timestamp with time zone), expires_at, absolute_expires_at));


--
-- Name: sessions_previous_refresh_token_hash_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX sessions_previous_refresh_token_hash_idx ON public.sessions USING btree (previous_refresh_token_hash) WHERE (previous_refresh_token_hash IS NOT NULL);


--
-- Name: sessions_refresh_token_hash_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX sessions_refresh_token_hash_key ON public.sessions USING btree (refresh_token_hash);


--
-- Name: sessions_user_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX sessions_user_id_idx ON public.sessions USING btree (user_id);


--
-- Name: station_bank_accounts_one_active; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX station_bank_accounts_one_active ON public.station_bank_accounts USING btree (station_id) WHERE (status = 'ACTIVE'::text);


--
-- Name: station_bank_accounts_one_pending; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX station_bank_accounts_one_pending ON public.station_bank_accounts USING btree (station_id) WHERE (status = 'PENDING'::text);


--
-- Name: station_bank_accounts_operator_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX station_bank_accounts_operator_id_idx ON public.station_bank_accounts USING btree (operator_id, id);


--
-- Name: station_bank_accounts_station_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX station_bank_accounts_station_id_idx ON public.station_bank_accounts USING btree (station_id, id);


--
-- Name: station_bank_accounts_status_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX station_bank_accounts_status_idx ON public.station_bank_accounts USING btree (status, id);


--
-- Name: station_operators_tax_id_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX station_operators_tax_id_key ON public.station_operators USING btree (public.tax_id_key(tax_id)) WHERE (tax_id IS NOT NULL);


--
-- Name: station_payables_account_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX station_payables_account_key ON public.station_payables USING btree (account_id);


--
-- Name: station_payables_operator_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX station_payables_operator_id_idx ON public.station_payables USING btree (operator_id);


--
-- Name: stations_operator_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX stations_operator_id_idx ON public.stations USING btree (operator_id, id);


--
-- Name: sync_batches_device_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX sync_batches_device_idx ON public.sync_batches USING btree (device_id, id);


--
-- Name: sync_batches_operator_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX sync_batches_operator_idx ON public.sync_batches USING btree (operator_id, id);


--
-- Name: ticket_messages_ticket_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX ticket_messages_ticket_idx ON public.ticket_messages USING btree (ticket_id, created_at);


--
-- Name: tickets_company_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX tickets_company_idx ON public.tickets USING btree (company_id, id);


--
-- Name: tickets_operator_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX tickets_operator_idx ON public.tickets USING btree (operator_id, id);


--
-- Name: tickets_reference_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX tickets_reference_key ON public.tickets USING btree (reference);


--
-- Name: tickets_status_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX tickets_status_idx ON public.tickets USING btree (status, id);


--
-- Name: topup_requests_awaiting_approval_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX topup_requests_awaiting_approval_idx ON public.topup_requests USING btree (id) WHERE (status = 'PENDING_APPROVAL'::text);


--
-- Name: topup_requests_company_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX topup_requests_company_id_idx ON public.topup_requests USING btree (company_id, id);


--
-- Name: topup_requests_ledger_transaction_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX topup_requests_ledger_transaction_key ON public.topup_requests USING btree (ledger_transaction_id) WHERE (ledger_transaction_id IS NOT NULL);


--
-- Name: topup_requests_reference_live_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX topup_requests_reference_live_key ON public.topup_requests USING btree (receiving_account, bank_reference_key) WHERE (status = ANY (ARRAY['PENDING'::text, 'PENDING_APPROVAL'::text, 'CONFIRMED'::text]));


--
-- Name: topup_requests_status_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX topup_requests_status_idx ON public.topup_requests USING btree (status, id);


--
-- Name: users_email_lower_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX users_email_lower_key ON public.users USING btree (lower(email));


--
-- Name: users_role_status_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX users_role_status_id_idx ON public.users USING btree (role, status, id);


--
-- Name: users_scope_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX users_scope_idx ON public.users USING btree (scope_type, scope_id);


--
-- Name: vehicles_company_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX vehicles_company_id_idx ON public.vehicles USING btree (company_id, id);


--
-- Name: vehicles_department_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX vehicles_department_id_idx ON public.vehicles USING btree (department_id);


--
-- Name: vehicles_fleet_code_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX vehicles_fleet_code_key ON public.vehicles USING btree (company_id, upper(fleet_code)) WHERE ((fleet_code IS NOT NULL) AND (status <> 'DECOMMISSIONED'::text));


--
-- Name: vehicles_plate_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX vehicles_plate_key ON public.vehicles USING btree (plate_key) WHERE (status <> 'DECOMMISSIONED'::text);


--
-- Name: wallet_adjustments_company_id_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX wallet_adjustments_company_id_idx ON public.wallet_adjustments USING btree (company_id, id);


--
-- Name: wallet_adjustments_ledger_transaction_key; Type: INDEX; Schema: public; Owner: postgres
--

CREATE UNIQUE INDEX wallet_adjustments_ledger_transaction_key ON public.wallet_adjustments USING btree (ledger_transaction_id) WHERE (ledger_transaction_id IS NOT NULL);


--
-- Name: wallet_adjustments_status_idx; Type: INDEX; Schema: public; Owner: postgres
--

CREATE INDEX wallet_adjustments_status_idx ON public.wallet_adjustments USING btree (status, id);


--
-- Name: agents agents_same_tenant; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER agents_same_tenant BEFORE INSERT OR UPDATE ON public.agents FOR EACH ROW EXECUTE FUNCTION public.check_same_tenant();


--
-- Name: fueling_reversals fueling_reversals_check_insert; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER fueling_reversals_check_insert BEFORE INSERT ON public.fueling_reversals FOR EACH ROW EXECUTE FUNCTION public.fueling_reversals_check_insert();


--
-- Name: fueling_reversals fueling_reversals_guard; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER fueling_reversals_guard BEFORE UPDATE ON public.fueling_reversals FOR EACH ROW EXECUTE FUNCTION public.fueling_reversals_guard();


--
-- Name: fuelings fuelings_immutable; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER fuelings_immutable BEFORE DELETE OR UPDATE ON public.fuelings FOR EACH ROW EXECUTE FUNCTION public.fuelings_refuse_change();


--
-- Name: fuelings fuelings_no_truncate; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER fuelings_no_truncate BEFORE TRUNCATE ON public.fuelings FOR EACH STATEMENT EXECUTE FUNCTION public.fuelings_refuse_change();


--
-- Name: fuelings fuelings_same_tenant; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER fuelings_same_tenant BEFORE INSERT ON public.fuelings FOR EACH ROW EXECUTE FUNCTION public.fuelings_same_tenant();


--
-- Name: ledger_entries ledger_entries_account; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER ledger_entries_account BEFORE INSERT ON public.ledger_entries FOR EACH ROW EXECUTE FUNCTION public.ledger_entry_check_account();


--
-- Name: ledger_entries ledger_entries_balanced; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE CONSTRAINT TRIGGER ledger_entries_balanced AFTER INSERT ON public.ledger_entries DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.ledger_check_balanced();


--
-- Name: ledger_entries ledger_entries_immutable; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER ledger_entries_immutable BEFORE DELETE OR UPDATE ON public.ledger_entries FOR EACH ROW EXECUTE FUNCTION public.ledger_refuse_change();


--
-- Name: ledger_entries ledger_entries_no_truncate; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER ledger_entries_no_truncate BEFORE TRUNCATE ON public.ledger_entries FOR EACH STATEMENT EXECUTE FUNCTION public.ledger_refuse_change();


--
-- Name: ledger_transactions ledger_transactions_balanced; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE CONSTRAINT TRIGGER ledger_transactions_balanced AFTER INSERT ON public.ledger_transactions DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.ledger_check_balanced();


--
-- Name: ledger_transactions ledger_transactions_immutable; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER ledger_transactions_immutable BEFORE DELETE OR UPDATE ON public.ledger_transactions FOR EACH ROW EXECUTE FUNCTION public.ledger_refuse_change();


--
-- Name: ledger_transactions ledger_transactions_no_truncate; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER ledger_transactions_no_truncate BEFORE TRUNCATE ON public.ledger_transactions FOR EACH STATEMENT EXECUTE FUNCTION public.ledger_refuse_change();


--
-- Name: limit_rules limit_rules_same_tenant; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER limit_rules_same_tenant BEFORE INSERT OR UPDATE ON public.limit_rules FOR EACH ROW EXECUTE FUNCTION public.check_same_tenant();


--
-- Name: notification_read_marks notification_read_marks_forward; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER notification_read_marks_forward BEFORE UPDATE ON public.notification_read_marks FOR EACH ROW EXECUTE FUNCTION public.notification_read_marks_forward();


--
-- Name: payable_hold_releases payable_hold_releases_check_insert; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER payable_hold_releases_check_insert BEFORE INSERT ON public.payable_hold_releases FOR EACH ROW EXECUTE FUNCTION public.payable_hold_releases_check_insert();


--
-- Name: payable_hold_releases payable_hold_releases_immutable; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER payable_hold_releases_immutable BEFORE DELETE OR UPDATE ON public.payable_hold_releases FOR EACH ROW EXECUTE FUNCTION public.ledger_refuse_change();


--
-- Name: payable_hold_releases payable_hold_releases_no_truncate; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER payable_hold_releases_no_truncate BEFORE TRUNCATE ON public.payable_hold_releases FOR EACH STATEMENT EXECUTE FUNCTION public.ledger_refuse_change();


--
-- Name: payouts payouts_check_insert; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER payouts_check_insert BEFORE INSERT ON public.payouts FOR EACH ROW EXECUTE FUNCTION public.payouts_check_insert();


--
-- Name: payouts payouts_guard; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER payouts_guard BEFORE UPDATE ON public.payouts FOR EACH ROW EXECUTE FUNCTION public.payouts_guard();


--
-- Name: pos_devices pos_devices_same_tenant; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER pos_devices_same_tenant BEFORE INSERT OR UPDATE ON public.pos_devices FOR EACH ROW EXECUTE FUNCTION public.check_same_tenant();


--
-- Name: rfid_tags rfid_tags_identity_guard; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER rfid_tags_identity_guard BEFORE UPDATE ON public.rfid_tags FOR EACH ROW EXECUTE FUNCTION public.fleet_identity_guard();


--
-- Name: rfid_tags rfid_tags_same_tenant; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER rfid_tags_same_tenant BEFORE INSERT OR UPDATE ON public.rfid_tags FOR EACH ROW EXECUTE FUNCTION public.check_same_tenant();


--
-- Name: station_bank_accounts station_bank_accounts_audit_rekey; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER station_bank_accounts_audit_rekey AFTER UPDATE OF account_encrypted ON public.station_bank_accounts FOR EACH ROW WHEN ((new.account_encrypted IS DISTINCT FROM old.account_encrypted)) EXECUTE FUNCTION public.station_bank_accounts_audit_rekey();


--
-- Name: station_bank_accounts station_bank_accounts_check_insert; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER station_bank_accounts_check_insert BEFORE INSERT ON public.station_bank_accounts FOR EACH ROW EXECUTE FUNCTION public.station_bank_accounts_check_insert();


--
-- Name: station_bank_accounts station_bank_accounts_guard; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER station_bank_accounts_guard BEFORE UPDATE ON public.station_bank_accounts FOR EACH ROW EXECUTE FUNCTION public.station_bank_accounts_guard();


--
-- Name: station_payables station_payables_same_operator; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER station_payables_same_operator BEFORE INSERT ON public.station_payables FOR EACH ROW EXECUTE FUNCTION public.station_finance_same_operator();


--
-- Name: topup_requests topup_requests_guard; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER topup_requests_guard BEFORE INSERT OR UPDATE ON public.topup_requests FOR EACH ROW EXECUTE FUNCTION public.topup_requests_guard();


--
-- Name: topup_requests topup_requests_no_delete; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER topup_requests_no_delete BEFORE DELETE OR TRUNCATE ON public.topup_requests FOR EACH STATEMENT EXECUTE FUNCTION public.ledger_refuse_change();


--
-- Name: vehicles vehicles_identity_guard; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER vehicles_identity_guard BEFORE UPDATE ON public.vehicles FOR EACH ROW EXECUTE FUNCTION public.fleet_identity_guard();


--
-- Name: vehicles vehicles_same_tenant; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER vehicles_same_tenant BEFORE INSERT OR UPDATE ON public.vehicles FOR EACH ROW EXECUTE FUNCTION public.check_same_tenant();


--
-- Name: wallet_adjustments wallet_adjustments_guard; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER wallet_adjustments_guard BEFORE INSERT OR UPDATE ON public.wallet_adjustments FOR EACH ROW EXECUTE FUNCTION public.wallet_adjustments_guard();


--
-- Name: wallet_adjustments wallet_adjustments_no_delete; Type: TRIGGER; Schema: public; Owner: postgres
--

CREATE TRIGGER wallet_adjustments_no_delete BEFORE DELETE OR TRUNCATE ON public.wallet_adjustments FOR EACH STATEMENT EXECUTE FUNCTION public.ledger_refuse_change();


--
-- Name: agent_sessions agent_sessions_agent_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.agent_sessions
    ADD CONSTRAINT agent_sessions_agent_id_fkey FOREIGN KEY (agent_id) REFERENCES public.agents(id);


--
-- Name: agent_sessions agent_sessions_device_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.agent_sessions
    ADD CONSTRAINT agent_sessions_device_id_fkey FOREIGN KEY (device_id) REFERENCES public.pos_devices(id);


--
-- Name: agent_sessions agent_sessions_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.agent_sessions
    ADD CONSTRAINT agent_sessions_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: agent_sessions agent_sessions_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.agent_sessions
    ADD CONSTRAINT agent_sessions_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: agents agents_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.agents
    ADD CONSTRAINT agents_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: agents agents_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.agents
    ADD CONSTRAINT agents_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: alerts alerts_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.alerts
    ADD CONSTRAINT alerts_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: alerts alerts_device_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.alerts
    ADD CONSTRAINT alerts_device_id_fkey FOREIGN KEY (device_id) REFERENCES public.pos_devices(id);


--
-- Name: alerts alerts_fueling_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.alerts
    ADD CONSTRAINT alerts_fueling_id_fkey FOREIGN KEY (fueling_id) REFERENCES public.fuelings(id);


--
-- Name: alerts alerts_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.alerts
    ADD CONSTRAINT alerts_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: alerts alerts_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.alerts
    ADD CONSTRAINT alerts_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: alerts alerts_vehicle_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.alerts
    ADD CONSTRAINT alerts_vehicle_id_fkey FOREIGN KEY (vehicle_id) REFERENCES public.vehicles(id);


--
-- Name: company_wallets company_wallets_account_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.company_wallets
    ADD CONSTRAINT company_wallets_account_id_fkey FOREIGN KEY (account_id) REFERENCES public.ledger_accounts(id);


--
-- Name: company_wallets company_wallets_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.company_wallets
    ADD CONSTRAINT company_wallets_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: departments departments_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.departments
    ADD CONSTRAINT departments_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: device_nonces device_nonces_device_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.device_nonces
    ADD CONSTRAINT device_nonces_device_id_fkey FOREIGN KEY (device_id) REFERENCES public.pos_devices(id);


--
-- Name: fleet_import_jobs fleet_import_jobs_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fleet_import_jobs
    ADD CONSTRAINT fleet_import_jobs_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: fleet_import_rows fleet_import_rows_job_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fleet_import_rows
    ADD CONSTRAINT fleet_import_rows_job_id_fkey FOREIGN KEY (job_id) REFERENCES public.fleet_import_jobs(id);


--
-- Name: fleet_import_rows fleet_import_rows_vehicle_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fleet_import_rows
    ADD CONSTRAINT fleet_import_rows_vehicle_id_fkey FOREIGN KEY (vehicle_id) REFERENCES public.vehicles(id);


--
-- Name: fuel_prices fuel_prices_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fuel_prices
    ADD CONSTRAINT fuel_prices_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: fueling_reversals fueling_reversals_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fueling_reversals
    ADD CONSTRAINT fueling_reversals_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: fueling_reversals fueling_reversals_fueling_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fueling_reversals
    ADD CONSTRAINT fueling_reversals_fueling_id_fkey FOREIGN KEY (fueling_id) REFERENCES public.fuelings(id);


--
-- Name: fueling_reversals fueling_reversals_ledger_transaction_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fueling_reversals
    ADD CONSTRAINT fueling_reversals_ledger_transaction_id_fkey FOREIGN KEY (ledger_transaction_id) REFERENCES public.ledger_transactions(id);


--
-- Name: fueling_reversals fueling_reversals_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fueling_reversals
    ADD CONSTRAINT fueling_reversals_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: fueling_reversals fueling_reversals_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fueling_reversals
    ADD CONSTRAINT fueling_reversals_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: fuelings fuelings_agent_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fuelings
    ADD CONSTRAINT fuelings_agent_id_fkey FOREIGN KEY (agent_id) REFERENCES public.agents(id);


--
-- Name: fuelings fuelings_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fuelings
    ADD CONSTRAINT fuelings_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: fuelings fuelings_device_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fuelings
    ADD CONSTRAINT fuelings_device_id_fkey FOREIGN KEY (device_id) REFERENCES public.pos_devices(id);


--
-- Name: fuelings fuelings_ledger_transaction_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fuelings
    ADD CONSTRAINT fuelings_ledger_transaction_id_fkey FOREIGN KEY (ledger_transaction_id) REFERENCES public.ledger_transactions(id);


--
-- Name: fuelings fuelings_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fuelings
    ADD CONSTRAINT fuelings_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: fuelings fuelings_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fuelings
    ADD CONSTRAINT fuelings_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: fuelings fuelings_tag_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fuelings
    ADD CONSTRAINT fuelings_tag_id_fkey FOREIGN KEY (tag_id) REFERENCES public.rfid_tags(id);


--
-- Name: fuelings fuelings_vehicle_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.fuelings
    ADD CONSTRAINT fuelings_vehicle_id_fkey FOREIGN KEY (vehicle_id) REFERENCES public.vehicles(id);


--
-- Name: ledger_accounts ledger_accounts_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ledger_accounts
    ADD CONSTRAINT ledger_accounts_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: ledger_accounts ledger_accounts_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ledger_accounts
    ADD CONSTRAINT ledger_accounts_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: ledger_entries ledger_entries_account_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ledger_entries
    ADD CONSTRAINT ledger_entries_account_id_fkey FOREIGN KEY (account_id) REFERENCES public.ledger_accounts(id);


--
-- Name: ledger_entries ledger_entries_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ledger_entries
    ADD CONSTRAINT ledger_entries_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: ledger_entries ledger_entries_transaction_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ledger_entries
    ADD CONSTRAINT ledger_entries_transaction_id_fkey FOREIGN KEY (transaction_id) REFERENCES public.ledger_transactions(id);


--
-- Name: ledger_transactions ledger_transactions_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ledger_transactions
    ADD CONSTRAINT ledger_transactions_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: ledger_transactions ledger_transactions_reverses_transaction_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ledger_transactions
    ADD CONSTRAINT ledger_transactions_reverses_transaction_id_fkey FOREIGN KEY (reverses_transaction_id) REFERENCES public.ledger_transactions(id);


--
-- Name: limit_rules limit_rules_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.limit_rules
    ADD CONSTRAINT limit_rules_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: limit_rules limit_rules_vehicle_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.limit_rules
    ADD CONSTRAINT limit_rules_vehicle_id_fkey FOREIGN KEY (vehicle_id) REFERENCES public.vehicles(id);


--
-- Name: mfa_challenges mfa_challenges_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.mfa_challenges
    ADD CONSTRAINT mfa_challenges_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id);


--
-- Name: notification_read_marks notification_read_marks_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.notification_read_marks
    ADD CONSTRAINT notification_read_marks_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id);


--
-- Name: notification_reads notification_reads_notification_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.notification_reads
    ADD CONSTRAINT notification_reads_notification_id_fkey FOREIGN KEY (notification_id) REFERENCES public.notifications(id) ON DELETE CASCADE;


--
-- Name: notification_reads notification_reads_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.notification_reads
    ADD CONSTRAINT notification_reads_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id);


--
-- Name: payable_hold_releases payable_hold_releases_fueling_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.payable_hold_releases
    ADD CONSTRAINT payable_hold_releases_fueling_id_fkey FOREIGN KEY (fueling_id) REFERENCES public.fuelings(id);


--
-- Name: payable_hold_releases payable_hold_releases_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.payable_hold_releases
    ADD CONSTRAINT payable_hold_releases_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: payable_hold_releases payable_hold_releases_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.payable_hold_releases
    ADD CONSTRAINT payable_hold_releases_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: payouts payouts_bank_account_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.payouts
    ADD CONSTRAINT payouts_bank_account_id_fkey FOREIGN KEY (bank_account_id) REFERENCES public.station_bank_accounts(id);


--
-- Name: payouts payouts_ledger_transaction_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.payouts
    ADD CONSTRAINT payouts_ledger_transaction_id_fkey FOREIGN KEY (ledger_transaction_id) REFERENCES public.ledger_transactions(id);


--
-- Name: payouts payouts_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.payouts
    ADD CONSTRAINT payouts_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: payouts payouts_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.payouts
    ADD CONSTRAINT payouts_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: pos_devices pos_devices_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.pos_devices
    ADD CONSTRAINT pos_devices_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: pos_devices pos_devices_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.pos_devices
    ADD CONSTRAINT pos_devices_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: reconciliation_findings reconciliation_findings_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.reconciliation_findings
    ADD CONSTRAINT reconciliation_findings_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: reconciliation_findings reconciliation_findings_run_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.reconciliation_findings
    ADD CONSTRAINT reconciliation_findings_run_id_fkey FOREIGN KEY (run_id) REFERENCES public.reconciliation_runs(id);


--
-- Name: reconciliation_findings reconciliation_findings_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.reconciliation_findings
    ADD CONSTRAINT reconciliation_findings_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: rfid_tags rfid_tags_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.rfid_tags
    ADD CONSTRAINT rfid_tags_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: rfid_tags rfid_tags_vehicle_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.rfid_tags
    ADD CONSTRAINT rfid_tags_vehicle_id_fkey FOREIGN KEY (vehicle_id) REFERENCES public.vehicles(id);


--
-- Name: sessions sessions_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.sessions
    ADD CONSTRAINT sessions_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id);


--
-- Name: station_bank_accounts station_bank_accounts_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.station_bank_accounts
    ADD CONSTRAINT station_bank_accounts_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: station_bank_accounts station_bank_accounts_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.station_bank_accounts
    ADD CONSTRAINT station_bank_accounts_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: station_payables station_payables_account_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.station_payables
    ADD CONSTRAINT station_payables_account_id_fkey FOREIGN KEY (account_id) REFERENCES public.ledger_accounts(id);


--
-- Name: station_payables station_payables_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.station_payables
    ADD CONSTRAINT station_payables_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: station_payables station_payables_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.station_payables
    ADD CONSTRAINT station_payables_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: stations stations_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.stations
    ADD CONSTRAINT stations_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: sync_batches sync_batches_device_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.sync_batches
    ADD CONSTRAINT sync_batches_device_id_fkey FOREIGN KEY (device_id) REFERENCES public.pos_devices(id);


--
-- Name: sync_batches sync_batches_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.sync_batches
    ADD CONSTRAINT sync_batches_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: sync_batches sync_batches_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.sync_batches
    ADD CONSTRAINT sync_batches_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: ticket_messages ticket_messages_ticket_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.ticket_messages
    ADD CONSTRAINT ticket_messages_ticket_id_fkey FOREIGN KEY (ticket_id) REFERENCES public.tickets(id);


--
-- Name: tickets tickets_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.tickets
    ADD CONSTRAINT tickets_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: tickets tickets_fueling_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.tickets
    ADD CONSTRAINT tickets_fueling_id_fkey FOREIGN KEY (fueling_id) REFERENCES public.fuelings(id);


--
-- Name: tickets tickets_operator_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.tickets
    ADD CONSTRAINT tickets_operator_id_fkey FOREIGN KEY (operator_id) REFERENCES public.station_operators(id);


--
-- Name: tickets tickets_station_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.tickets
    ADD CONSTRAINT tickets_station_id_fkey FOREIGN KEY (station_id) REFERENCES public.stations(id);


--
-- Name: tickets tickets_vehicle_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.tickets
    ADD CONSTRAINT tickets_vehicle_id_fkey FOREIGN KEY (vehicle_id) REFERENCES public.vehicles(id);


--
-- Name: topup_requests topup_requests_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.topup_requests
    ADD CONSTRAINT topup_requests_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: topup_requests topup_requests_ledger_transaction_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.topup_requests
    ADD CONSTRAINT topup_requests_ledger_transaction_id_fkey FOREIGN KEY (ledger_transaction_id) REFERENCES public.ledger_transactions(id);


--
-- Name: vehicles vehicles_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.vehicles
    ADD CONSTRAINT vehicles_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: vehicles vehicles_department_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.vehicles
    ADD CONSTRAINT vehicles_department_id_fkey FOREIGN KEY (department_id) REFERENCES public.departments(id);


--
-- Name: wallet_adjustments wallet_adjustments_company_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.wallet_adjustments
    ADD CONSTRAINT wallet_adjustments_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id);


--
-- Name: wallet_adjustments wallet_adjustments_ledger_transaction_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: postgres
--

ALTER TABLE ONLY public.wallet_adjustments
    ADD CONSTRAINT wallet_adjustments_ledger_transaction_id_fkey FOREIGN KEY (ledger_transaction_id) REFERENCES public.ledger_transactions(id);


--
-- Name: agent_sessions; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.agent_sessions ENABLE ROW LEVEL SECURITY;

--
-- Name: agent_sessions agent_sessions_operator_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY agent_sessions_operator_read ON public.agent_sessions FOR SELECT USING (((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id())));


--
-- Name: agent_sessions agent_sessions_platform; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY agent_sessions_platform ON public.agent_sessions USING (public.app_is_platform()) WITH CHECK (public.app_is_platform());


--
-- Name: agents; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.agents ENABLE ROW LEVEL SECURITY;

--
-- Name: agents agents_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY agents_by_scope ON public.agents USING ((public.app_is_platform() OR ((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: alerts; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.alerts ENABLE ROW LEVEL SECURITY;

--
-- Name: alerts alerts_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY alerts_by_scope ON public.alerts USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id()) AND company_visible))) WITH CHECK (public.app_is_platform());


--
-- Name: audit_log; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.audit_log ENABLE ROW LEVEL SECURITY;

--
-- Name: audit_log audit_log_append; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY audit_log_append ON public.audit_log FOR INSERT WITH CHECK ((public.app_is_platform() OR ((scope_type = public.app_scope_type()) AND (scope_id = public.app_scope_id()))));


--
-- Name: audit_log audit_log_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY audit_log_read ON public.audit_log FOR SELECT USING ((public.app_is_platform() OR ((scope_type = public.app_scope_type()) AND (scope_id = public.app_scope_id()))));


--
-- Name: companies; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.companies ENABLE ROW LEVEL SECURITY;

--
-- Name: companies companies_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY companies_by_scope ON public.companies USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: company_wallets; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.company_wallets ENABLE ROW LEVEL SECURITY;

--
-- Name: company_wallets company_wallets_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY company_wallets_by_scope ON public.company_wallets USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: departments; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.departments ENABLE ROW LEVEL SECURITY;

--
-- Name: departments departments_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY departments_by_scope ON public.departments USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: device_nonces; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.device_nonces ENABLE ROW LEVEL SECURITY;

--
-- Name: device_nonces device_nonces_platform; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY device_nonces_platform ON public.device_nonces USING (public.app_is_platform()) WITH CHECK (public.app_is_platform());


--
-- Name: fleet_import_jobs; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.fleet_import_jobs ENABLE ROW LEVEL SECURITY;

--
-- Name: fleet_import_jobs fleet_import_jobs_platform; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY fleet_import_jobs_platform ON public.fleet_import_jobs USING (public.app_is_platform()) WITH CHECK (public.app_is_platform());


--
-- Name: fleet_import_rows; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.fleet_import_rows ENABLE ROW LEVEL SECURITY;

--
-- Name: fleet_import_rows fleet_import_rows_platform; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY fleet_import_rows_platform ON public.fleet_import_rows USING (public.app_is_platform()) WITH CHECK (public.app_is_platform());


--
-- Name: fuel_prices; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.fuel_prices ENABLE ROW LEVEL SECURITY;

--
-- Name: fuel_prices fuel_prices_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY fuel_prices_read ON public.fuel_prices FOR SELECT USING ((public.app_scope_type() IS NOT NULL));


--
-- Name: fuel_prices fuel_prices_schedule; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY fuel_prices_schedule ON public.fuel_prices FOR INSERT WITH CHECK (public.app_is_platform());


--
-- Name: fueling_reversals; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.fueling_reversals ENABLE ROW LEVEL SECURITY;

--
-- Name: fueling_reversals fueling_reversals_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY fueling_reversals_by_scope ON public.fueling_reversals USING ((public.app_is_platform() OR ((status = 'APPROVED'::text) AND (((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id())) OR ((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id())))))) WITH CHECK (public.app_is_platform());


--
-- Name: fuelings; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.fuelings ENABLE ROW LEVEL SECURITY;

--
-- Name: fuelings fuelings_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY fuelings_by_scope ON public.fuelings USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id())) OR ((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: idempotency_keys; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.idempotency_keys ENABLE ROW LEVEL SECURITY;

--
-- Name: idempotency_keys idempotency_keys_own; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY idempotency_keys_own ON public.idempotency_keys USING ((actor_id = public.app_user_id())) WITH CHECK ((actor_id = public.app_user_id()));


--
-- Name: job_runs; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.job_runs ENABLE ROW LEVEL SECURITY;

--
-- Name: job_runs job_runs_platform_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY job_runs_platform_read ON public.job_runs FOR SELECT USING (public.app_is_platform());


--
-- Name: leads; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.leads ENABLE ROW LEVEL SECURITY;

--
-- Name: leads leads_platform_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY leads_platform_read ON public.leads FOR SELECT USING ((public.app_is_platform() AND (public.app_user_id() IS NOT NULL)));


--
-- Name: leads leads_platform_update; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY leads_platform_update ON public.leads FOR UPDATE USING ((public.app_is_platform() AND (public.app_user_id() IS NOT NULL))) WITH CHECK ((public.app_is_platform() AND (public.app_user_id() IS NOT NULL) AND (updated_by = public.app_user_id())));


--
-- Name: ledger_accounts; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.ledger_accounts ENABLE ROW LEVEL SECURITY;

--
-- Name: ledger_accounts ledger_accounts_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY ledger_accounts_by_scope ON public.ledger_accounts USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: ledger_accounts ledger_accounts_operator_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY ledger_accounts_operator_read ON public.ledger_accounts FOR SELECT USING (((public.app_scope_type() = 'OPERATOR'::text) AND (id IN ( SELECT p.account_id
   FROM public.station_payables p
  WHERE (p.operator_id = public.app_scope_id())))));


--
-- Name: ledger_entries; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.ledger_entries ENABLE ROW LEVEL SECURITY;

--
-- Name: ledger_entries ledger_entries_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY ledger_entries_by_scope ON public.ledger_entries USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: ledger_entries ledger_entries_operator_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY ledger_entries_operator_read ON public.ledger_entries FOR SELECT USING (((public.app_scope_type() = 'OPERATOR'::text) AND (account_id IN ( SELECT p.account_id
   FROM public.station_payables p
  WHERE (p.operator_id = public.app_scope_id())))));


--
-- Name: ledger_transactions; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.ledger_transactions ENABLE ROW LEVEL SECURITY;

--
-- Name: ledger_transactions ledger_transactions_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY ledger_transactions_by_scope ON public.ledger_transactions USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: ledger_transactions ledger_transactions_operator_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY ledger_transactions_operator_read ON public.ledger_transactions FOR SELECT USING (((public.app_scope_type() = 'OPERATOR'::text) AND (EXISTS ( SELECT 1
   FROM public.ledger_entries e
  WHERE ((e.transaction_id = ledger_transactions.id) AND (e.account_id IN ( SELECT p.account_id
           FROM public.station_payables p
          WHERE (p.operator_id = public.app_scope_id()))))))));


--
-- Name: limit_rules; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.limit_rules ENABLE ROW LEVEL SECURITY;

--
-- Name: limit_rules limit_rules_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY limit_rules_by_scope ON public.limit_rules USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: notification_read_marks; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.notification_read_marks ENABLE ROW LEVEL SECURITY;

--
-- Name: notification_read_marks notification_read_marks_move; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY notification_read_marks_move ON public.notification_read_marks FOR UPDATE USING ((user_id = ( SELECT public.app_user_id() AS app_user_id))) WITH CHECK ((user_id = ( SELECT public.app_user_id() AS app_user_id)));


--
-- Name: notification_read_marks notification_read_marks_own; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY notification_read_marks_own ON public.notification_read_marks FOR SELECT USING ((user_id = ( SELECT public.app_user_id() AS app_user_id)));


--
-- Name: notification_read_marks notification_read_marks_set; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY notification_read_marks_set ON public.notification_read_marks FOR INSERT WITH CHECK ((user_id = ( SELECT public.app_user_id() AS app_user_id)));


--
-- Name: notification_reads; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.notification_reads ENABLE ROW LEVEL SECURITY;

--
-- Name: notification_reads notification_reads_mark; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY notification_reads_mark ON public.notification_reads FOR INSERT WITH CHECK (((user_id = ( SELECT public.app_user_id() AS app_user_id)) AND (EXISTS ( SELECT 1
   FROM public.notifications n
  WHERE (n.id = notification_reads.notification_id)))));


--
-- Name: notification_reads notification_reads_own; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY notification_reads_own ON public.notification_reads FOR SELECT USING ((user_id = ( SELECT public.app_user_id() AS app_user_id)));


--
-- Name: notifications; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.notifications ENABLE ROW LEVEL SECURITY;

--
-- Name: notifications notifications_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY notifications_read ON public.notifications FOR SELECT USING (((audience_roles @> ARRAY[( SELECT public.app_user_role() AS app_user_role)]) AND (actor_id IS DISTINCT FROM ( SELECT public.app_user_id() AS app_user_id)) AND
CASE audience_scope_type
    WHEN 'PLATFORM'::text THEN ( SELECT public.app_is_platform() AS app_is_platform)
    ELSE ((audience_scope_type = ( SELECT public.app_scope_type() AS app_scope_type)) AND (audience_scope_id = ( SELECT public.app_scope_id() AS app_scope_id)))
END));


--
-- Name: notifications notifications_write; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY notifications_write ON public.notifications FOR INSERT WITH CHECK (((public.app_scope_type() IS NOT NULL) AND (NOT (actor_id IS DISTINCT FROM public.app_user_id())) AND
CASE
    WHEN public.app_is_platform() THEN ((public.app_user_id() IS NOT NULL) OR (type = ANY (ARRAY['WALLET_LOW_BALANCE'::text, 'HARD_ALERT_RAISED'::text])))
    ELSE ((public.app_user_id() IS NOT NULL) AND ((audience_scope_type = 'PLATFORM'::text) OR ((audience_scope_type = public.app_scope_type()) AND (audience_scope_id = public.app_scope_id()))))
END));


--
-- Name: payable_hold_releases; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.payable_hold_releases ENABLE ROW LEVEL SECURITY;

--
-- Name: payable_hold_releases payable_hold_releases_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY payable_hold_releases_by_scope ON public.payable_hold_releases USING ((public.app_is_platform() OR ((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: payouts; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.payouts ENABLE ROW LEVEL SECURITY;

--
-- Name: payouts payouts_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY payouts_by_scope ON public.payouts USING ((public.app_is_platform() OR ((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id()) AND (status = 'POSTED'::text)))) WITH CHECK (public.app_is_platform());


--
-- Name: pos_devices; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.pos_devices ENABLE ROW LEVEL SECURITY;

--
-- Name: pos_devices pos_devices_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY pos_devices_by_scope ON public.pos_devices USING ((public.app_is_platform() OR ((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: rate_limit_counters; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.rate_limit_counters ENABLE ROW LEVEL SECURITY;

--
-- Name: reconciliation_findings; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.reconciliation_findings ENABLE ROW LEVEL SECURITY;

--
-- Name: reconciliation_findings reconciliation_findings_platform; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY reconciliation_findings_platform ON public.reconciliation_findings FOR SELECT USING (public.app_is_platform());


--
-- Name: reconciliation_runs; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.reconciliation_runs ENABLE ROW LEVEL SECURITY;

--
-- Name: reconciliation_runs reconciliation_runs_platform; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY reconciliation_runs_platform ON public.reconciliation_runs FOR SELECT USING (public.app_is_platform());


--
-- Name: rfid_tags; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.rfid_tags ENABLE ROW LEVEL SECURITY;

--
-- Name: rfid_tags rfid_tags_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY rfid_tags_by_scope ON public.rfid_tags USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: station_bank_accounts; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.station_bank_accounts ENABLE ROW LEVEL SECURITY;

--
-- Name: station_bank_accounts station_bank_accounts_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY station_bank_accounts_by_scope ON public.station_bank_accounts USING ((public.app_is_platform() OR ((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id()) AND (status = ANY (ARRAY['ACTIVE'::text, 'PENDING'::text]))))) WITH CHECK (public.app_is_platform());


--
-- Name: station_operators; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.station_operators ENABLE ROW LEVEL SECURITY;

--
-- Name: station_operators station_operators_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY station_operators_by_scope ON public.station_operators USING ((public.app_is_platform() OR ((public.app_scope_type() = 'OPERATOR'::text) AND (id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: station_payables; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.station_payables ENABLE ROW LEVEL SECURITY;

--
-- Name: station_payables station_payables_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY station_payables_by_scope ON public.station_payables USING ((public.app_is_platform() OR ((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: stations; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.stations ENABLE ROW LEVEL SECURITY;

--
-- Name: stations stations_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY stations_by_scope ON public.stations USING ((public.app_is_platform() OR ((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: sync_batches; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.sync_batches ENABLE ROW LEVEL SECURITY;

--
-- Name: sync_batches sync_batches_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY sync_batches_by_scope ON public.sync_batches USING ((public.app_is_platform() OR ((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: ticket_messages; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.ticket_messages ENABLE ROW LEVEL SECURITY;

--
-- Name: ticket_messages ticket_messages_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY ticket_messages_read ON public.ticket_messages FOR SELECT USING ((public.app_is_platform() OR ((NOT internal) AND (EXISTS ( SELECT 1
   FROM public.tickets t
  WHERE (t.id = ticket_messages.ticket_id))))));


--
-- Name: ticket_messages ticket_messages_write; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY ticket_messages_write ON public.ticket_messages FOR INSERT WITH CHECK ((public.app_is_platform() OR ((NOT internal) AND (EXISTS ( SELECT 1
   FROM public.tickets t
  WHERE (t.id = ticket_messages.ticket_id))))));


--
-- Name: tickets; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.tickets ENABLE ROW LEVEL SECURITY;

--
-- Name: tickets tickets_open; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY tickets_open ON public.tickets FOR INSERT WITH CHECK ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (scope_type = 'COMPANY'::text) AND (company_id = public.app_scope_id())) OR ((public.app_scope_type() = 'OPERATOR'::text) AND (scope_type = 'OPERATOR'::text) AND (operator_id = public.app_scope_id()))));


--
-- Name: tickets tickets_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY tickets_read ON public.tickets FOR SELECT USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id())) OR ((public.app_scope_type() = 'OPERATOR'::text) AND (operator_id = public.app_scope_id()))));


--
-- Name: tickets tickets_update; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY tickets_update ON public.tickets FOR UPDATE USING (public.app_is_platform()) WITH CHECK (public.app_is_platform());


--
-- Name: topup_requests; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.topup_requests ENABLE ROW LEVEL SECURITY;

--
-- Name: topup_requests topup_requests_decide; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY topup_requests_decide ON public.topup_requests FOR UPDATE USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id()) AND (status = 'PENDING'::text)))) WITH CHECK ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id()) AND (status = 'CANCELLED'::text))));


--
-- Name: topup_requests topup_requests_read; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY topup_requests_read ON public.topup_requests FOR SELECT USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id()))));


--
-- Name: topup_requests topup_requests_request; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY topup_requests_request ON public.topup_requests FOR INSERT WITH CHECK ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id()) AND (status = 'PENDING'::text))));


--
-- Name: users; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.users ENABLE ROW LEVEL SECURITY;

--
-- Name: users users_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY users_by_scope ON public.users USING ((public.app_is_platform() OR ((scope_type = public.app_scope_type()) AND (scope_id = public.app_scope_id())))) WITH CHECK ((public.app_is_platform() OR ((scope_type = public.app_scope_type()) AND (scope_id = public.app_scope_id()))));


--
-- Name: vehicles; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.vehicles ENABLE ROW LEVEL SECURITY;

--
-- Name: vehicles vehicles_by_scope; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY vehicles_by_scope ON public.vehicles USING ((public.app_is_platform() OR ((public.app_scope_type() = 'COMPANY'::text) AND (company_id = public.app_scope_id())))) WITH CHECK (public.app_is_platform());


--
-- Name: wallet_adjustments; Type: ROW SECURITY; Schema: public; Owner: postgres
--

ALTER TABLE public.wallet_adjustments ENABLE ROW LEVEL SECURITY;

--
-- Name: wallet_adjustments wallet_adjustments_platform; Type: POLICY; Schema: public; Owner: postgres
--

CREATE POLICY wallet_adjustments_platform ON public.wallet_adjustments USING (public.app_is_platform()) WITH CHECK (public.app_is_platform());


--
-- Name: SCHEMA public; Type: ACL; Schema: -; Owner: pg_database_owner
--

GRANT USAGE ON SCHEMA public TO zawed_app;


--
-- Name: FUNCTION auth_bootstrap_super_admin(p_id uuid, p_email text, p_full_name text, p_password_hash text, p_now timestamp with time zone, p_audit_id uuid, p_request_id text, p_ip_prefix text); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.auth_bootstrap_super_admin(p_id uuid, p_email text, p_full_name text, p_password_hash text, p_now timestamp with time zone, p_audit_id uuid, p_request_id text, p_ip_prefix text) FROM PUBLIC;
GRANT ALL ON FUNCTION public.auth_bootstrap_super_admin(p_id uuid, p_email text, p_full_name text, p_password_hash text, p_now timestamp with time zone, p_audit_id uuid, p_request_id text, p_ip_prefix text) TO zawed_app;


--
-- Name: FUNCTION auth_login_lookup(p_email text); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.auth_login_lookup(p_email text) FROM PUBLIC;
GRANT ALL ON FUNCTION public.auth_login_lookup(p_email text) TO zawed_app;


--
-- Name: FUNCTION auth_record_login_failure(p_user_id uuid, p_count boolean, p_reason text, p_max_attempts integer, p_lock_seconds integer, p_now timestamp with time zone, p_audit_id uuid, p_lock_audit_id uuid, p_request_id text, p_ip_prefix text); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.auth_record_login_failure(p_user_id uuid, p_count boolean, p_reason text, p_max_attempts integer, p_lock_seconds integer, p_now timestamp with time zone, p_audit_id uuid, p_lock_audit_id uuid, p_request_id text, p_ip_prefix text) FROM PUBLIC;
GRANT ALL ON FUNCTION public.auth_record_login_failure(p_user_id uuid, p_count boolean, p_reason text, p_max_attempts integer, p_lock_seconds integer, p_now timestamp with time zone, p_audit_id uuid, p_lock_audit_id uuid, p_request_id text, p_ip_prefix text) TO zawed_app;


--
-- Name: FUNCTION auth_session_by_id(p_session_id uuid); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.auth_session_by_id(p_session_id uuid) FROM PUBLIC;
GRANT ALL ON FUNCTION public.auth_session_by_id(p_session_id uuid) TO zawed_app;


--
-- Name: FUNCTION auth_session_by_refresh_hash(p_hash text); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.auth_session_by_refresh_hash(p_hash text) FROM PUBLIC;
GRANT ALL ON FUNCTION public.auth_session_by_refresh_hash(p_hash text) TO zawed_app;


--
-- Name: FUNCTION auth_super_admin_exists(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.auth_super_admin_exists() FROM PUBLIC;
GRANT ALL ON FUNCTION public.auth_super_admin_exists() TO zawed_app;


--
-- Name: FUNCTION check_same_tenant(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.check_same_tenant() FROM PUBLIC;


--
-- Name: FUNCTION contact_digits(p_value text); Type: ACL; Schema: public; Owner: postgres
--

GRANT ALL ON FUNCTION public.contact_digits(p_value text) TO zawed_app;


--
-- Name: FUNCTION data_key_current_id(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.data_key_current_id() FROM PUBLIC;
GRANT ALL ON FUNCTION public.data_key_current_id() TO zawed_app;


--
-- Name: FUNCTION data_key_id(p_value bytea); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.data_key_id(p_value bytea) FROM PUBLIC;
GRANT ALL ON FUNCTION public.data_key_id(p_value bytea) TO zawed_app;


--
-- Name: FUNCTION data_key_rekeyed(p_old bytea, p_new bytea); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.data_key_rekeyed(p_old bytea, p_new bytea) FROM PUBLIC;
GRANT ALL ON FUNCTION public.data_key_rekeyed(p_old bytea, p_new bytea) TO zawed_app;


--
-- Name: FUNCTION fleet_identity_guard(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.fleet_identity_guard() FROM PUBLIC;


--
-- Name: FUNCTION fueling_reversals_check_insert(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.fueling_reversals_check_insert() FROM PUBLIC;


--
-- Name: FUNCTION fueling_reversals_guard(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.fueling_reversals_guard() FROM PUBLIC;


--
-- Name: FUNCTION fuelings_same_tenant(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.fuelings_same_tenant() FROM PUBLIC;


--
-- Name: FUNCTION job_run_latest(p_names text[]); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.job_run_latest(p_names text[]) FROM PUBLIC;
GRANT ALL ON FUNCTION public.job_run_latest(p_names text[]) TO zawed_app;


--
-- Name: FUNCTION job_run_record(p_id uuid, p_job text, p_instance text, p_started timestamp with time zone, p_finished timestamp with time zone, p_outcome text, p_counts jsonb, p_error_class text); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.job_run_record(p_id uuid, p_job text, p_instance text, p_started timestamp with time zone, p_finished timestamp with time zone, p_outcome text, p_counts jsonb, p_error_class text) FROM PUBLIC;
GRANT ALL ON FUNCTION public.job_run_record(p_id uuid, p_job text, p_instance text, p_started timestamp with time zone, p_finished timestamp with time zone, p_outcome text, p_counts jsonb, p_error_class text) TO zawed_app;


--
-- Name: FUNCTION ledger_check_balanced(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.ledger_check_balanced() FROM PUBLIC;


--
-- Name: FUNCTION ledger_entry_check_account(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.ledger_entry_check_account() FROM PUBLIC;


--
-- Name: TABLE reconciliation_runs; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT ON TABLE public.reconciliation_runs TO zawed_app;


--
-- Name: FUNCTION ledger_reconcile(p_run_id uuid, p_trigger text, p_actor uuid, p_actor_role text, p_now timestamp with time zone, p_request_id text); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.ledger_reconcile(p_run_id uuid, p_trigger text, p_actor uuid, p_actor_role text, p_now timestamp with time zone, p_request_id text) FROM PUBLIC;
GRANT ALL ON FUNCTION public.ledger_reconcile(p_run_id uuid, p_trigger text, p_actor uuid, p_actor_role text, p_now timestamp with time zone, p_request_id text) TO zawed_app;


--
-- Name: FUNCTION payable_hold_releases_check_insert(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.payable_hold_releases_check_insert() FROM PUBLIC;


--
-- Name: FUNCTION payouts_check_insert(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.payouts_check_insert() FROM PUBLIC;


--
-- Name: FUNCTION payouts_guard(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.payouts_guard() FROM PUBLIC;


--
-- Name: FUNCTION purge_closed_leads(p_before timestamp with time zone, p_limit integer); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.purge_closed_leads(p_before timestamp with time zone, p_limit integer) FROM PUBLIC;
GRANT ALL ON FUNCTION public.purge_closed_leads(p_before timestamp with time zone, p_limit integer) TO zawed_app;


--
-- Name: FUNCTION purge_device_nonces(p_before timestamp with time zone, p_limit integer); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.purge_device_nonces(p_before timestamp with time zone, p_limit integer) FROM PUBLIC;
GRANT ALL ON FUNCTION public.purge_device_nonces(p_before timestamp with time zone, p_limit integer) TO zawed_app;


--
-- Name: FUNCTION purge_idempotency_keys(p_before timestamp with time zone, p_limit integer); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.purge_idempotency_keys(p_before timestamp with time zone, p_limit integer) FROM PUBLIC;
GRANT ALL ON FUNCTION public.purge_idempotency_keys(p_before timestamp with time zone, p_limit integer) TO zawed_app;


--
-- Name: FUNCTION purge_job_runs(p_before timestamp with time zone, p_limit integer); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.purge_job_runs(p_before timestamp with time zone, p_limit integer) FROM PUBLIC;
GRANT ALL ON FUNCTION public.purge_job_runs(p_before timestamp with time zone, p_limit integer) TO zawed_app;


--
-- Name: FUNCTION purge_mfa_challenges(p_before timestamp with time zone, p_limit integer); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.purge_mfa_challenges(p_before timestamp with time zone, p_limit integer) FROM PUBLIC;
GRANT ALL ON FUNCTION public.purge_mfa_challenges(p_before timestamp with time zone, p_limit integer) TO zawed_app;


--
-- Name: FUNCTION purge_notifications(p_before timestamp with time zone, p_limit integer); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.purge_notifications(p_before timestamp with time zone, p_limit integer) FROM PUBLIC;
GRANT ALL ON FUNCTION public.purge_notifications(p_before timestamp with time zone, p_limit integer) TO zawed_app;


--
-- Name: FUNCTION purge_rate_limit_counters(p_limit integer); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.purge_rate_limit_counters(p_limit integer) FROM PUBLIC;
GRANT ALL ON FUNCTION public.purge_rate_limit_counters(p_limit integer) TO zawed_app;


--
-- Name: FUNCTION purge_sessions(p_before timestamp with time zone, p_limit integer); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.purge_sessions(p_before timestamp with time zone, p_limit integer) FROM PUBLIC;
GRANT ALL ON FUNCTION public.purge_sessions(p_before timestamp with time zone, p_limit integer) TO zawed_app;


--
-- Name: FUNCTION rate_limit_hit(p_bucket text, p_key_hash text, p_window_seconds integer); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.rate_limit_hit(p_bucket text, p_key_hash text, p_window_seconds integer) FROM PUBLIC;
GRANT ALL ON FUNCTION public.rate_limit_hit(p_bucket text, p_key_hash text, p_window_seconds integer) TO zawed_app;


--
-- Name: FUNCTION station_bank_accounts_audit_rekey(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.station_bank_accounts_audit_rekey() FROM PUBLIC;


--
-- Name: FUNCTION station_bank_accounts_check_insert(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.station_bank_accounts_check_insert() FROM PUBLIC;


--
-- Name: FUNCTION station_bank_accounts_guard(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.station_bank_accounts_guard() FROM PUBLIC;


--
-- Name: FUNCTION station_finance_same_operator(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.station_finance_same_operator() FROM PUBLIC;


--
-- Name: FUNCTION submit_lead(p_id uuid, p_company_name text, p_contact_name text, p_phone text, p_email text, p_fleet_size text, p_locale text); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.submit_lead(p_id uuid, p_company_name text, p_contact_name text, p_phone text, p_email text, p_fleet_size text, p_locale text) FROM PUBLIC;
GRANT ALL ON FUNCTION public.submit_lead(p_id uuid, p_company_name text, p_contact_name text, p_phone text, p_email text, p_fleet_size text, p_locale text) TO zawed_app;


--
-- Name: FUNCTION tax_id_key(p_tax_id text); Type: ACL; Schema: public; Owner: postgres
--

GRANT ALL ON FUNCTION public.tax_id_key(p_tax_id text) TO zawed_app;


--
-- Name: FUNCTION topup_requests_guard(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.topup_requests_guard() FROM PUBLIC;


--
-- Name: FUNCTION wallet_adjustments_guard(); Type: ACL; Schema: public; Owner: postgres
--

REVOKE ALL ON FUNCTION public.wallet_adjustments_guard() FROM PUBLIC;


--
-- Name: TABLE agent_sessions; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.agent_sessions TO zawed_app;


--
-- Name: COLUMN agent_sessions.last_seen_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(last_seen_at) ON TABLE public.agent_sessions TO zawed_app;


--
-- Name: COLUMN agent_sessions.revoked_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(revoked_at) ON TABLE public.agent_sessions TO zawed_app;


--
-- Name: COLUMN agent_sessions.revoke_reason; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(revoke_reason) ON TABLE public.agent_sessions TO zawed_app;


--
-- Name: TABLE agents; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.agents TO zawed_app;


--
-- Name: TABLE alerts; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.alerts TO zawed_app;


--
-- Name: COLUMN alerts.status; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(status) ON TABLE public.alerts TO zawed_app;


--
-- Name: COLUMN alerts.acknowledged_by; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(acknowledged_by) ON TABLE public.alerts TO zawed_app;


--
-- Name: COLUMN alerts.acknowledged_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(acknowledged_at) ON TABLE public.alerts TO zawed_app;


--
-- Name: TABLE audit_log; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.audit_log TO zawed_app;


--
-- Name: TABLE companies; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.companies TO zawed_app;


--
-- Name: TABLE company_wallets; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.company_wallets TO zawed_app;


--
-- Name: COLUMN company_wallets.balance_piastres; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(balance_piastres) ON TABLE public.company_wallets TO zawed_app;


--
-- Name: COLUMN company_wallets.updated_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(updated_at) ON TABLE public.company_wallets TO zawed_app;


--
-- Name: TABLE departments; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.departments TO zawed_app;


--
-- Name: TABLE device_nonces; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.device_nonces TO zawed_app;


--
-- Name: TABLE fleet_import_jobs; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.status; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(status) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.mode; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(mode) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.imported_count; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(imported_count) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.existing_count; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(existing_count) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.failed_count; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(failed_count) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.confirmed_by; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(confirmed_by) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.confirmed_by_role; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(confirmed_by_role) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.confirmed_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(confirmed_at) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.started_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(started_at) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.finished_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(finished_at) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.lease_owner; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(lease_owner) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.lease_until; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(lease_until) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: COLUMN fleet_import_jobs.updated_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(updated_at) ON TABLE public.fleet_import_jobs TO zawed_app;


--
-- Name: TABLE fleet_import_rows; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.fleet_import_rows TO zawed_app;


--
-- Name: COLUMN fleet_import_rows.status; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(status) ON TABLE public.fleet_import_rows TO zawed_app;


--
-- Name: COLUMN fleet_import_rows.errors; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(errors) ON TABLE public.fleet_import_rows TO zawed_app;


--
-- Name: COLUMN fleet_import_rows.vehicle_id; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(vehicle_id) ON TABLE public.fleet_import_rows TO zawed_app;


--
-- Name: COLUMN fleet_import_rows.processed_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(processed_at) ON TABLE public.fleet_import_rows TO zawed_app;


--
-- Name: TABLE flyway_schema_history; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT ON TABLE public.flyway_schema_history TO zawed_app;


--
-- Name: TABLE fuel_prices; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.fuel_prices TO zawed_app;


--
-- Name: SEQUENCE fueling_reference_seq; Type: ACL; Schema: public; Owner: postgres
--

GRANT USAGE ON SEQUENCE public.fueling_reference_seq TO zawed_app;


--
-- Name: TABLE fueling_reversals; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.fueling_reversals TO zawed_app;


--
-- Name: COLUMN fueling_reversals.status; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(status) ON TABLE public.fueling_reversals TO zawed_app;


--
-- Name: COLUMN fueling_reversals.decided_by; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decided_by) ON TABLE public.fueling_reversals TO zawed_app;


--
-- Name: COLUMN fueling_reversals.decided_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decided_at) ON TABLE public.fueling_reversals TO zawed_app;


--
-- Name: COLUMN fueling_reversals.decision_reason; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decision_reason) ON TABLE public.fueling_reversals TO zawed_app;


--
-- Name: COLUMN fueling_reversals.ledger_transaction_id; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(ledger_transaction_id) ON TABLE public.fueling_reversals TO zawed_app;


--
-- Name: COLUMN fueling_reversals.updated_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(updated_at) ON TABLE public.fueling_reversals TO zawed_app;


--
-- Name: TABLE fuelings; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.fuelings TO zawed_app;


--
-- Name: TABLE idempotency_keys; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.idempotency_keys TO zawed_app;


--
-- Name: TABLE job_runs; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT ON TABLE public.job_runs TO zawed_app;


--
-- Name: TABLE leads; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT ON TABLE public.leads TO zawed_app;


--
-- Name: COLUMN leads.status; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(status) ON TABLE public.leads TO zawed_app;


--
-- Name: COLUMN leads.note; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(note) ON TABLE public.leads TO zawed_app;


--
-- Name: COLUMN leads.updated_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(updated_at) ON TABLE public.leads TO zawed_app;


--
-- Name: COLUMN leads.updated_by; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(updated_by) ON TABLE public.leads TO zawed_app;


--
-- Name: COLUMN leads.closed_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(closed_at) ON TABLE public.leads TO zawed_app;


--
-- Name: TABLE ledger_accounts; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.ledger_accounts TO zawed_app;


--
-- Name: TABLE ledger_entries; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.ledger_entries TO zawed_app;


--
-- Name: TABLE ledger_transactions; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.ledger_transactions TO zawed_app;


--
-- Name: TABLE limit_rules; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.limit_rules TO zawed_app;


--
-- Name: TABLE mfa_challenges; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.mfa_challenges TO zawed_app;


--
-- Name: TABLE notification_read_marks; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.notification_read_marks TO zawed_app;


--
-- Name: TABLE notification_reads; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.notification_reads TO zawed_app;


--
-- Name: TABLE notifications; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.notifications TO zawed_app;


--
-- Name: TABLE payable_hold_releases; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.payable_hold_releases TO zawed_app;


--
-- Name: TABLE payouts; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.payouts TO zawed_app;


--
-- Name: COLUMN payouts.status; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(status) ON TABLE public.payouts TO zawed_app;


--
-- Name: COLUMN payouts.decided_by; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decided_by) ON TABLE public.payouts TO zawed_app;


--
-- Name: COLUMN payouts.decided_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decided_at) ON TABLE public.payouts TO zawed_app;


--
-- Name: COLUMN payouts.decision_reason; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decision_reason) ON TABLE public.payouts TO zawed_app;


--
-- Name: COLUMN payouts.posted_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(posted_at) ON TABLE public.payouts TO zawed_app;


--
-- Name: COLUMN payouts.ledger_transaction_id; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(ledger_transaction_id) ON TABLE public.payouts TO zawed_app;


--
-- Name: COLUMN payouts.updated_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(updated_at) ON TABLE public.payouts TO zawed_app;


--
-- Name: TABLE pos_devices; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.pos_devices TO zawed_app;


--
-- Name: TABLE reconciliation_findings; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT ON TABLE public.reconciliation_findings TO zawed_app;


--
-- Name: TABLE rfid_tags; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.rfid_tags TO zawed_app;


--
-- Name: TABLE sessions; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.sessions TO zawed_app;


--
-- Name: TABLE station_bank_accounts; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.station_bank_accounts TO zawed_app;


--
-- Name: COLUMN station_bank_accounts.account_encrypted; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(account_encrypted) ON TABLE public.station_bank_accounts TO zawed_app;


--
-- Name: COLUMN station_bank_accounts.status; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(status) ON TABLE public.station_bank_accounts TO zawed_app;


--
-- Name: COLUMN station_bank_accounts.decided_by; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decided_by) ON TABLE public.station_bank_accounts TO zawed_app;


--
-- Name: COLUMN station_bank_accounts.decided_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decided_at) ON TABLE public.station_bank_accounts TO zawed_app;


--
-- Name: COLUMN station_bank_accounts.decision_reason; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decision_reason) ON TABLE public.station_bank_accounts TO zawed_app;


--
-- Name: COLUMN station_bank_accounts.payouts_held_until; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(payouts_held_until) ON TABLE public.station_bank_accounts TO zawed_app;


--
-- Name: COLUMN station_bank_accounts.superseded_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(superseded_at) ON TABLE public.station_bank_accounts TO zawed_app;


--
-- Name: COLUMN station_bank_accounts.updated_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(updated_at) ON TABLE public.station_bank_accounts TO zawed_app;


--
-- Name: TABLE station_operators; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.station_operators TO zawed_app;


--
-- Name: TABLE station_payables; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.station_payables TO zawed_app;


--
-- Name: COLUMN station_payables.balance_piastres; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(balance_piastres) ON TABLE public.station_payables TO zawed_app;


--
-- Name: COLUMN station_payables.updated_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(updated_at) ON TABLE public.station_payables TO zawed_app;


--
-- Name: TABLE stations; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.stations TO zawed_app;


--
-- Name: TABLE sync_batches; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.sync_batches TO zawed_app;


--
-- Name: TABLE ticket_messages; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.ticket_messages TO zawed_app;


--
-- Name: SEQUENCE ticket_reference_seq; Type: ACL; Schema: public; Owner: postgres
--

GRANT USAGE ON SEQUENCE public.ticket_reference_seq TO zawed_app;


--
-- Name: TABLE tickets; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.tickets TO zawed_app;


--
-- Name: COLUMN tickets.status; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(status) ON TABLE public.tickets TO zawed_app;


--
-- Name: COLUMN tickets.updated_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(updated_at) ON TABLE public.tickets TO zawed_app;


--
-- Name: COLUMN tickets.assigned_to; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(assigned_to) ON TABLE public.tickets TO zawed_app;


--
-- Name: COLUMN tickets.resolved_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(resolved_at) ON TABLE public.tickets TO zawed_app;


--
-- Name: TABLE topup_requests; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.topup_requests TO zawed_app;


--
-- Name: TABLE users; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.users TO zawed_app;


--
-- Name: TABLE vehicles; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT,UPDATE ON TABLE public.vehicles TO zawed_app;


--
-- Name: TABLE wallet_adjustments; Type: ACL; Schema: public; Owner: postgres
--

GRANT SELECT,INSERT ON TABLE public.wallet_adjustments TO zawed_app;


--
-- Name: COLUMN wallet_adjustments.status; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(status) ON TABLE public.wallet_adjustments TO zawed_app;


--
-- Name: COLUMN wallet_adjustments.decided_by; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decided_by) ON TABLE public.wallet_adjustments TO zawed_app;


--
-- Name: COLUMN wallet_adjustments.decided_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decided_at) ON TABLE public.wallet_adjustments TO zawed_app;


--
-- Name: COLUMN wallet_adjustments.decision_reason; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(decision_reason) ON TABLE public.wallet_adjustments TO zawed_app;


--
-- Name: COLUMN wallet_adjustments.ledger_transaction_id; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(ledger_transaction_id) ON TABLE public.wallet_adjustments TO zawed_app;


--
-- Name: COLUMN wallet_adjustments.updated_at; Type: ACL; Schema: public; Owner: postgres
--

GRANT UPDATE(updated_at) ON TABLE public.wallet_adjustments TO zawed_app;


--
-- PostgreSQL database dump complete
--

\unrestrict dRAk496aPeaXY42YEJjSEwwhsdwU2jx5LGeexdVALGBh7p1SEX6khmRl6BC9Omo

