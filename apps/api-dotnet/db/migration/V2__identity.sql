-- Identity and audit (phase B1): users, sessions, MFA challenges, the append-only audit log.
--
-- Rules from V1 apply: uuid v7 keys made by the application, timestamptz in UTC, every foreign key
-- and every filtered or sorted column indexed, rows never physically removed (status columns).
-- Row-level security, the runtime role and its grants follow in V3.

CREATE TABLE users (
  id                     uuid PRIMARY KEY,
  email                  text NOT NULL,
  full_name              text NOT NULL,
  password_hash          text NOT NULL,
  role                   text NOT NULL
    CHECK (role IN ('SUPER_ADMIN', 'OPS', 'FLEET_MANAGER', 'ACCOUNTANT', 'STATION_MANAGER')),
  scope_type             text NOT NULL CHECK (scope_type IN ('PLATFORM', 'COMPANY', 'OPERATOR')),
  scope_id               uuid,
  status                 text NOT NULL CHECK (status IN ('ACTIVE', 'SUSPENDED', 'DEACTIVATED')),
  must_change_password   boolean NOT NULL DEFAULT false,
  failed_login_count     integer NOT NULL DEFAULT 0 CHECK (failed_login_count >= 0),
  locked_until           timestamptz,
  mfa_secret_encrypted   bytea,
  mfa_enabled            boolean NOT NULL DEFAULT false,
  mfa_last_used_step     bigint,
  password_changed_at    timestamptz,
  created_at             timestamptz NOT NULL,
  created_by             uuid,
  updated_at             timestamptz NOT NULL,
  updated_by             uuid,
  -- A platform user has no scope id; a scoped user always has one.
  CONSTRAINT users_scope_id_matches_type CHECK ((scope_type = 'PLATFORM') = (scope_id IS NULL)),
  -- The role decides the kind of scope (PRD section 3, contract section 2).
  CONSTRAINT users_role_matches_scope CHECK (
       (role IN ('SUPER_ADMIN', 'OPS') AND scope_type = 'PLATFORM')
    OR (role IN ('FLEET_MANAGER', 'ACCOUNTANT') AND scope_type = 'COMPANY')
    OR (role = 'STATION_MANAGER' AND scope_type = 'OPERATOR')),
  -- An enabled second factor always has a secret.
  CONSTRAINT users_mfa_secret_present CHECK (NOT mfa_enabled OR mfa_secret_encrypted IS NOT NULL)
);

-- Case-insensitive uniqueness without an extension; the application stores emails lower-cased.
CREATE UNIQUE INDEX users_email_lower_key ON users (lower(email));
-- GET /admin/users filters on role and status and pages by id (uuid v7 is time-ordered).
CREATE INDEX users_role_status_id_idx ON users (role, status, id);
CREATE INDEX users_scope_idx ON users (scope_type, scope_id);

COMMENT ON TABLE users IS 'Web portal users (PRD 3). Never deleted: status DEACTIVATED instead.';
COMMENT ON COLUMN users.mfa_secret_encrypted IS 'TOTP secret, AES-256-GCM with a key id byte (platform.crypto.DataEncryptor).';
COMMENT ON COLUMN users.mfa_last_used_step IS 'Highest TOTP time step accepted; a step is never accepted twice.';

CREATE TABLE sessions (
  id                           uuid PRIMARY KEY,
  user_id                      uuid NOT NULL REFERENCES users (id),
  -- Which sign-in door created it: the admin cookie is separate from the user cookie.
  door                         text NOT NULL CHECK (door IN ('USER', 'ADMIN')),
  refresh_token_hash           text NOT NULL,
  previous_refresh_token_hash  text,
  rotated_at                   timestamptz,
  created_at                   timestamptz NOT NULL,
  last_seen_at                 timestamptz NOT NULL,
  expires_at                   timestamptz NOT NULL,
  absolute_expires_at          timestamptz NOT NULL,
  revoked_at                   timestamptz,
  revoked_reason               text,
  user_agent                   text,
  -- A truncated client address (/24 or /48), never the full one.
  ip_prefix                    text
);

CREATE INDEX sessions_user_id_idx ON sessions (user_id);
CREATE UNIQUE INDEX sessions_refresh_token_hash_key ON sessions (refresh_token_hash);
CREATE INDEX sessions_previous_refresh_token_hash_idx ON sessions (previous_refresh_token_hash)
  WHERE previous_refresh_token_hash IS NOT NULL;

COMMENT ON TABLE sessions IS 'One row per sign-in. Checked on every request; revoked rows stay for the record.';

CREATE TABLE mfa_challenges (
  id                        uuid PRIMARY KEY,
  user_id                   uuid NOT NULL REFERENCES users (id),
  purpose                   text NOT NULL CHECK (purpose IN ('VERIFY', 'ENROLL')),
  door                      text NOT NULL CHECK (door IN ('USER', 'ADMIN')),
  token_hash                text NOT NULL,
  -- Snapshot of the user's scope when the password was accepted; binds the second step's
  -- database context to the same user.
  scope_type                text NOT NULL CHECK (scope_type IN ('PLATFORM', 'COMPANY', 'OPERATOR')),
  scope_id                  uuid,
  pending_secret_encrypted  bytea,
  attempts                  integer NOT NULL DEFAULT 0,
  created_at                timestamptz NOT NULL,
  expires_at                timestamptz NOT NULL,
  used_at                   timestamptz,
  user_agent                text,
  ip_prefix                 text
);

CREATE INDEX mfa_challenges_user_id_idx ON mfa_challenges (user_id);
CREATE UNIQUE INDEX mfa_challenges_token_hash_key ON mfa_challenges (token_hash);

COMMENT ON TABLE mfa_challenges IS 'Second sign-in step after a correct password. Single use, short-lived, bound to one user.';

CREATE TABLE audit_log (
  id            uuid PRIMARY KEY,
  occurred_at   timestamptz NOT NULL,
  actor_id      uuid,
  actor_role    text,
  action        text NOT NULL,
  entity_type   text NOT NULL,
  entity_id     uuid,
  scope_type    text NOT NULL CHECK (scope_type IN ('PLATFORM', 'COMPANY', 'OPERATOR')),
  scope_id      uuid,
  metadata      jsonb NOT NULL DEFAULT '{}'::jsonb,
  request_id    text,
  ip_prefix     text,
  CONSTRAINT audit_log_scope_id_matches_type CHECK ((scope_type = 'PLATFORM') = (scope_id IS NULL))
);

-- actor_id is deliberately not a foreign key: an audit row must never be refused because of the
-- state of another table, and users are never deleted anyway.
CREATE INDEX audit_log_actor_idx ON audit_log (actor_id, id);
CREATE INDEX audit_log_entity_idx ON audit_log (entity_type, entity_id, id);
CREATE INDEX audit_log_occurred_at_idx ON audit_log (occurred_at);
CREATE INDEX audit_log_scope_idx ON audit_log (scope_type, scope_id, id);

COMMENT ON TABLE audit_log IS 'Append only. Written in the same transaction as the change it records. Never updated or deleted.';
