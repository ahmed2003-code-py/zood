-- The runtime role, its grants, row-level security and the narrow doors the sign-in path uses.
--
-- Railway and the embedded test database connect as a superuser, which bypasses row-level
-- security. So Flyway keeps running as that owner, and the application switches every pooled
-- connection to zawed_app (Hikari connection-init-sql: SET ROLE zawed_app), a NOLOGIN role that
-- owns nothing and holds only the grants below. Every later migration that adds a table must
-- grant zawed_app what it needs, explicitly; nothing is granted by default.
--
-- The per-transaction scope is set by the application with set_config(..., true) from the
-- authenticated principal (platform.security.ScopedTransactionManager). The policies read it
-- through the app_* functions. With nothing set, a scoped table shows no rows.

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'zawed_app') THEN
    CREATE ROLE zawed_app NOLOGIN;
  END IF;
END
$$;

-- The connecting user must be allowed to SET ROLE zawed_app. A superuser always may; this makes
-- it work for an ordinary owner role too.
GRANT zawed_app TO CURRENT_USER;

GRANT USAGE ON SCHEMA public TO zawed_app;
GRANT SELECT ON flyway_schema_history TO zawed_app;          -- GET /api/v1/meta reads the schema version
GRANT SELECT, INSERT, UPDATE ON users TO zawed_app;          -- no DELETE: users are deactivated
GRANT SELECT, INSERT, UPDATE ON sessions TO zawed_app;       -- no DELETE: revoked rows stay
GRANT SELECT, INSERT, UPDATE ON mfa_challenges TO zawed_app;
GRANT SELECT, INSERT ON audit_log TO zawed_app;              -- append only: no UPDATE, no DELETE

-- ---------------------------------------------------------------------------------------------
-- The transaction's scope, as the policies see it.
-- ---------------------------------------------------------------------------------------------

CREATE FUNCTION app_scope_type() RETURNS text
  LANGUAGE sql STABLE PARALLEL SAFE
  AS $$ SELECT nullif(current_setting('app.scope_type', true), '') $$;

CREATE FUNCTION app_scope_id() RETURNS uuid
  LANGUAGE sql STABLE PARALLEL SAFE
  AS $$ SELECT nullif(current_setting('app.scope_id', true), '')::uuid $$;

CREATE FUNCTION app_user_id() RETURNS uuid
  LANGUAGE sql STABLE PARALLEL SAFE
  AS $$ SELECT nullif(current_setting('app.user_id', true), '')::uuid $$;

CREATE FUNCTION app_is_platform() RETURNS boolean
  LANGUAGE sql STABLE PARALLEL SAFE
  AS $$ SELECT coalesce(app_scope_type() = 'PLATFORM', false) $$;

-- ---------------------------------------------------------------------------------------------
-- Row-level security. FORCE so that even the owner is subject to it when it is not a superuser.
-- ---------------------------------------------------------------------------------------------

ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE users FORCE ROW LEVEL SECURITY;

-- A platform scope sees every user. A company or operator scope sees the users of that one
-- company or operator, and can only write rows that stay inside it.
CREATE POLICY users_by_scope ON users
  USING (app_is_platform() OR (scope_type = app_scope_type() AND scope_id = app_scope_id()))
  WITH CHECK (app_is_platform() OR (scope_type = app_scope_type() AND scope_id = app_scope_id()));

ALTER TABLE audit_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_log FORCE ROW LEVEL SECURITY;

CREATE POLICY audit_log_read ON audit_log FOR SELECT
  USING (app_is_platform() OR (scope_type = app_scope_type() AND scope_id = app_scope_id()));

-- An audit row carries the scope of the entity it is about, so a company can later see what was
-- done to its own users; a platform actor may write rows about any scope.
CREATE POLICY audit_log_append ON audit_log FOR INSERT
  WITH CHECK (app_is_platform() OR (scope_type = app_scope_type() AND scope_id = app_scope_id()));

-- No UPDATE or DELETE policy exists, and zawed_app has no such grant: both fail for everyone
-- but the owner.

-- ---------------------------------------------------------------------------------------------
-- The sign-in path has no principal yet, so it cannot set a scope. Instead of switching row-level
-- security off, it goes through these SECURITY DEFINER functions, each returning or changing only
-- what that step needs. They are owned by the migration user and callable by zawed_app alone.
-- ---------------------------------------------------------------------------------------------

-- Login: everything the password check and the lockout need, by email.
CREATE FUNCTION auth_login_lookup(p_email text)
  RETURNS TABLE (
    id uuid, email text, full_name text, password_hash text, role text, scope_type text,
    scope_id uuid, status text, must_change_password boolean, failed_login_count integer,
    locked_until timestamptz, mfa_enabled boolean)
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp
  AS $$
    SELECT u.id, u.email, u.full_name, u.password_hash, u.role, u.scope_type, u.scope_id,
           u.status, u.must_change_password, u.failed_login_count, u.locked_until, u.mfa_enabled
    FROM users u
    WHERE lower(u.email) = lower(p_email)
  $$;

-- A failed attempt: counts it (when p_count), locks after p_max_attempts, and writes the audit
-- rows in the same transaction. Returns when the lock ends, or null when the account is not locked.
CREATE FUNCTION auth_record_login_failure(
    p_user_id uuid, p_count boolean, p_reason text, p_max_attempts integer, p_lock_seconds integer,
    p_now timestamptz, p_audit_id uuid, p_lock_audit_id uuid, p_request_id text, p_ip_prefix text)
  RETURNS timestamptz
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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

-- A session with its user, for the check every request makes and for refresh and logout.
CREATE TYPE auth_session_row AS (
  session_id uuid, user_id uuid, door text, refresh_token_hash text,
  previous_refresh_token_hash text, rotated_at timestamptz, created_at timestamptz,
  last_seen_at timestamptz, expires_at timestamptz, absolute_expires_at timestamptz,
  revoked_at timestamptz, email text, full_name text, role text, scope_type text, scope_id uuid,
  status text, must_change_password boolean, mfa_enabled boolean
);

CREATE FUNCTION auth_session_by_id(p_session_id uuid)
  RETURNS SETOF auth_session_row
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp
  AS $$
    SELECT s.id, s.user_id, s.door, s.refresh_token_hash, s.previous_refresh_token_hash,
           s.rotated_at, s.created_at, s.last_seen_at, s.expires_at, s.absolute_expires_at,
           s.revoked_at, u.email, u.full_name, u.role, u.scope_type, u.scope_id, u.status,
           u.must_change_password, u.mfa_enabled
    FROM sessions s JOIN users u ON u.id = s.user_id
    WHERE s.id = p_session_id
  $$;

-- By the hash of a presented refresh token: the current one or the one it replaced (race grace).
CREATE FUNCTION auth_session_by_refresh_hash(p_hash text)
  RETURNS SETOF auth_session_row
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp
  AS $$
    SELECT s.id, s.user_id, s.door, s.refresh_token_hash, s.previous_refresh_token_hash,
           s.rotated_at, s.created_at, s.last_seen_at, s.expires_at, s.absolute_expires_at,
           s.revoked_at, u.email, u.full_name, u.role, u.scope_type, u.scope_id, u.status,
           u.must_change_password, u.mfa_enabled
    FROM sessions s JOIN users u ON u.id = s.user_id
    WHERE s.refresh_token_hash = p_hash OR s.previous_refresh_token_hash = p_hash
  $$;

CREATE FUNCTION auth_super_admin_exists()
  RETURNS boolean
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp
  AS $$ SELECT EXISTS (SELECT 1 FROM users WHERE role = 'SUPER_ADMIN') $$;

-- First Super Admin. Serialised with an advisory lock so two concurrent attempts cannot both
-- succeed; returns false when one already exists. Audited in the same transaction.
CREATE FUNCTION auth_bootstrap_super_admin(
    p_id uuid, p_email text, p_full_name text, p_password_hash text, p_now timestamptz,
    p_audit_id uuid, p_request_id text, p_ip_prefix text)
  RETURNS boolean
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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

REVOKE ALL ON FUNCTION auth_login_lookup(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_record_login_failure(uuid, boolean, text, integer, integer, timestamptz, uuid, uuid, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_session_by_id(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_session_by_refresh_hash(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_super_admin_exists() FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_bootstrap_super_admin(uuid, text, text, text, timestamptz, uuid, text, text) FROM PUBLIC;

GRANT EXECUTE ON FUNCTION auth_login_lookup(text) TO zawed_app;
GRANT EXECUTE ON FUNCTION auth_record_login_failure(uuid, boolean, text, integer, integer, timestamptz, uuid, uuid, text, text) TO zawed_app;
GRANT EXECUTE ON FUNCTION auth_session_by_id(uuid) TO zawed_app;
GRANT EXECUTE ON FUNCTION auth_session_by_refresh_hash(text) TO zawed_app;
GRANT EXECUTE ON FUNCTION auth_super_admin_exists() TO zawed_app;
GRANT EXECUTE ON FUNCTION auth_bootstrap_super_admin(uuid, text, text, text, timestamptz, uuid, text, text) TO zawed_app;
