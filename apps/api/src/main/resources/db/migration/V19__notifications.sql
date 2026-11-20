-- In-app notifications (B7 work stream W5; owner decision 2026-11-19: in-app only, no e-mail yet).
--
-- A notification is addressed to an audience, not to a person: a scope (the platform, one company
-- or one operator) and the roles inside it that should hear about it. Whoever holds such a role in
-- that scope sees it; a user who joins later sees what is still kept. What each user has read is
-- their own row in notification_reads.
--
-- A notification is written by the notifications module, in the transaction of the change that
-- caused it (a domain event heard synchronously), so it exists if and only if its cause committed.
-- It carries no text: a type, the entity it points at, and a few parameters that are ids, integers
-- (piastres, millilitres) or enum codes. The web writes the sentence from its own translations, so
-- no name, plate, reason or other free text is copied here, and nothing here needs translating.
--
-- V17 and V18 are reserved by parallel B7 work; this migration depends on neither.

-- ---------------------------------------------------------------------------------------------
-- Parameters: ids, integers and codes only.
-- ---------------------------------------------------------------------------------------------

-- At most 8 keys, each a camelCase name; each value an integer of up to 19 digits (piastres,
-- millilitres, counts), a lower-case UUID, or an upper-case code (an enum name such as an alert
-- type). Anything else, a sentence or a name in particular, is refused.
CREATE FUNCTION notification_params_valid(p jsonb) RETURNS boolean
  LANGUAGE sql IMMUTABLE PARALLEL SAFE
  AS $$
    SELECT CASE
      WHEN p IS NULL OR jsonb_typeof(p) <> 'object' THEN false
      ELSE (SELECT count(*) FROM jsonb_object_keys(p)) <= 8
       AND NOT EXISTS (
             SELECT 1 FROM jsonb_each(p) AS e(key, value)
             WHERE e.key !~ '^[a-z][A-Za-z0-9]{0,39}$'
                OR NOT CASE jsonb_typeof(e.value)
                         WHEN 'number' THEN e.value::text ~ '^-?[0-9]{1,19}$'
                         WHEN 'string' THEN (e.value #>> '{}')
                           ~ '^([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[A-Z][A-Z0-9_]{0,63})$'
                         ELSE false
                       END)
    END
  $$;

-- ---------------------------------------------------------------------------------------------
-- Tables
-- ---------------------------------------------------------------------------------------------

CREATE TABLE notifications (
  id                  uuid PRIMARY KEY,
  type                text NOT NULL CHECK (type IN (
                        'WALLET_LOW_BALANCE', 'TOPUP_CREDITED', 'TOPUP_REJECTED', 'TICKET_ANSWERED',
                        'TOPUP_AWAITING_APPROVAL', 'WALLET_ADJUSTMENT_AWAITING_APPROVAL', 'HARD_ALERT_RAISED',
                        'TICKET_OPENED', 'PAYOUT_POSTED', 'BANK_ACCOUNT_CHANGE_APPROVED',
                        'BANK_ACCOUNT_CHANGE_REJECTED')),
  audience_scope_type text NOT NULL CHECK (audience_scope_type IN ('PLATFORM', 'COMPANY', 'OPERATOR')),
  -- The company or operator id; null for the platform. Not a foreign key: a notification is a
  -- record of something that happened, and a foreign key would lock the parent row on every write.
  audience_scope_id   uuid,
  audience_roles      text[] NOT NULL,
  entity_type         text NOT NULL CHECK (entity_type IN (
                        'COMPANY_WALLET', 'TOPUP', 'WALLET_ADJUSTMENT', 'ALERT', 'TICKET', 'PAYOUT',
                        'STATION_BANK_ACCOUNT')),
  entity_id           uuid NOT NULL,
  params              jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (notification_params_valid(params)),
  -- The user whose action caused it (app.user_id of the causing transaction), null for a device or
  -- the system. That user is not told about their own action: an Ops Manager who verified a top-up
  -- cannot approve it, so a "waiting for your approval" notice would only be noise.
  actor_id            uuid,
  created_at          timestamptz NOT NULL,
  CONSTRAINT notifications_audience_scope CHECK ((audience_scope_type = 'PLATFORM') = (audience_scope_id IS NULL)),
  -- Only roles that can exist in the audience's kind of scope (platform.security.Role).
  CONSTRAINT notifications_audience_roles CHECK (
    cardinality(audience_roles) >= 1
    AND audience_roles <@ CASE audience_scope_type
          WHEN 'PLATFORM' THEN ARRAY['SUPER_ADMIN', 'OPS_MANAGER', 'OPS']
          WHEN 'COMPANY' THEN ARRAY['FLEET_MANAGER', 'ACCOUNTANT']
          WHEN 'OPERATOR' THEN ARRAY['STATION_MANAGER']
        END)
);

-- "My notifications, newest first": the audience, then the id (UUIDv7, so id order is time order).
-- A platform audience has a null scope id, which a b-tree finds with IS NULL.
CREATE INDEX notifications_audience_idx ON notifications (audience_scope_type, audience_scope_id, id);
-- The retention purge.
CREATE INDEX notifications_created_at_idx ON notifications (created_at);

CREATE TABLE notification_reads (
  user_id          uuid NOT NULL REFERENCES users (id),
  -- Purged with its notification (the purge function runs as the owner; zawed_app deletes nothing).
  notification_id  uuid NOT NULL REFERENCES notifications (id) ON DELETE CASCADE,
  read_at          timestamptz NOT NULL,
  PRIMARY KEY (user_id, notification_id)
);

-- The primary key answers "has this user read it"; this one serves the cascade from the purge.
CREATE INDEX notification_reads_notification_idx ON notification_reads (notification_id);

COMMENT ON TABLE notifications IS
  'In-app notifications addressed to a scope and roles; ids, integers and codes only. Append only for zawed_app.';
COMMENT ON TABLE notification_reads IS
  'Per-user read state of notifications: one row per user and notification read. Append only for zawed_app.';

-- Append only for the application: a notification is never edited, and a read is never unread.
-- Neither table gets UPDATE or DELETE; the retention purge goes through purge_notifications below.
GRANT SELECT, INSERT ON notifications TO zawed_app;
GRANT SELECT, INSERT ON notification_reads TO zawed_app;

-- ---------------------------------------------------------------------------------------------
-- Row-level security
-- ---------------------------------------------------------------------------------------------

-- The acting user's role, as the database holds it now. Runs as the caller, so the users policy
-- applies: a user always sees their own row (same scope), and with no user set it is null.
CREATE FUNCTION app_user_role() RETURNS text
  LANGUAGE sql STABLE PARALLEL SAFE
  AS $$ SELECT u.role FROM users u WHERE u.id = app_user_id() $$;

-- A user reads the notifications addressed to their scope and their role, never the ones their own
-- action caused. Unlike business data, a platform user does not see every scope's notifications:
-- a company's notices are for the company. The function calls are wrapped in SELECT so each runs
-- once per query, not once per row.
ALTER TABLE notifications ENABLE ROW LEVEL SECURITY;
CREATE POLICY notifications_read ON notifications FOR SELECT
  USING (
    audience_roles @> ARRAY[(SELECT app_user_role())]
    AND actor_id IS DISTINCT FROM (SELECT app_user_id())
    AND CASE audience_scope_type
          WHEN 'PLATFORM' THEN (SELECT app_is_platform())
          ELSE audience_scope_type = (SELECT app_scope_type()) AND audience_scope_id = (SELECT app_scope_id())
        END);

-- Written in the causing transaction, whatever its scope: the platform may address anyone; a
-- company or operator may address the platform (a ticket it opened) or itself, never another
-- tenant. The actor is the transaction's own user, so it cannot be forged. Without a scope,
-- nothing is written; a company or operator scope always has a user (only the platform runs
-- without one: a device, a job), so without one it writes nothing either.
CREATE POLICY notifications_write ON notifications FOR INSERT
  WITH CHECK (
    app_scope_type() IS NOT NULL
    AND actor_id IS NOT DISTINCT FROM app_user_id()
    AND (app_is_platform()
         OR (app_user_id() IS NOT NULL
             AND (audience_scope_type = 'PLATFORM'
                  OR (audience_scope_type = app_scope_type() AND audience_scope_id = app_scope_id())))));

-- Each user reads and writes only their own read state, and only for a notification they can see.
ALTER TABLE notification_reads ENABLE ROW LEVEL SECURITY;
CREATE POLICY notification_reads_own ON notification_reads FOR SELECT
  USING (user_id = (SELECT app_user_id()));
CREATE POLICY notification_reads_mark ON notification_reads FOR INSERT
  WITH CHECK (user_id = (SELECT app_user_id())
              AND EXISTS (SELECT 1 FROM notifications n WHERE n.id = notification_id));

-- ---------------------------------------------------------------------------------------------
-- Retention
-- ---------------------------------------------------------------------------------------------

-- Deletes up to p_limit notifications created before p_before, oldest first, with their read rows
-- (cascade). Called by notifications.NotificationRetention in short batches. Refuses anything
-- younger than 30 days, whatever the application is configured with. SKIP LOCKED lets two
-- instances running the job at once share the work instead of waiting on each other.
CREATE FUNCTION purge_notifications(p_before timestamptz, p_limit integer) RETURNS integer
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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
REVOKE ALL ON FUNCTION purge_notifications(timestamptz, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION purge_notifications(timestamptz, integer) TO zawed_app;
