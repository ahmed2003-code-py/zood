-- In-app notifications (B7 work stream W5; owner decision 2026-11-19: in-app only, no e-mail yet).
--
-- A notification is addressed to an audience, not to a person: a scope (the platform, one company
-- or one operator) and the roles inside it that should hear about it. Whoever holds such a role in
-- that scope sees it; a user who joins later sees what is still kept. What each user has read is
-- their own: a "read up to" mark (notification_read_marks) plus a row in notification_reads for
-- each one read after it.
--
-- A notification is written by the notifications module, in the transaction of the change that
-- caused it (a domain event heard synchronously), inside a savepoint: it exists only if its cause
-- committed, and a notification the database refuses is rolled back to the savepoint and counted
-- (zawed.notifications.dropped) while its cause still commits. A fueling is never lost to a notice.
-- It carries no text: a type, the entity it points at, and a few parameters that are ids, integers
-- (piastres, millilitres) or enum codes. The web writes the sentence from its own translations, so
-- no name, plate, reason or other free text is copied here, and nothing here needs translating.
--
-- V17 is B7 W1 (top-up guards); V18 is never used. This migration depends on neither.

-- ---------------------------------------------------------------------------------------------
-- Parameters: ids, integers and codes only.
-- ---------------------------------------------------------------------------------------------

-- The keys each type may carry (contract §2; B7 W5 review, L2). Notifications.Type holds the same
-- lists, and NotificationWriteRulesTests checks the two agree. A key may be left out when it does not apply (an
-- alert without a fueling), never added: a new key is a contract change.
CREATE FUNCTION notification_param_keys(p_type text) RETURNS text[]
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

-- Only the type's own keys (above); each value an integer of up to 19 digits (piastres,
-- millilitres, counts), a lower-case UUID, or an upper-case code (an enum name such as an alert
-- type). Anything else, a sentence or a name in particular, is refused.
CREATE FUNCTION notification_params_valid(p_type text, p jsonb) RETURNS boolean
  LANGUAGE sql IMMUTABLE PARALLEL SAFE
  AS $$
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
  params              jsonb NOT NULL DEFAULT '{}'::jsonb,
  -- The user whose action caused it (app.user_id of the causing transaction), null for a device or
  -- the system. That user is not told about their own action: an Ops Manager who verified a top-up
  -- cannot approve it, so a "waiting for your approval" notice would only be noise.
  actor_id            uuid,
  created_at          timestamptz NOT NULL,
  CONSTRAINT notifications_params CHECK (notification_params_valid(type, params)),
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

-- "My notifications, newest first" and "how many since my mark": the audience, then the keyset
-- (created_at, id). The feed pages with the row comparison (created_at, id) < (cursor), and the
-- unread count ranges over created_at > the user's mark, both as index conditions, so neither
-- reads what is older than it needs. A platform audience has a null scope id, which a b-tree finds
-- with IS NULL.
CREATE INDEX notifications_audience_idx ON notifications (audience_scope_type, audience_scope_id, created_at, id);
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

-- "Mark all read" as one row per user instead of one row per notification (B7 W5 review, M2):
-- everything addressed to the user and created at or before read_up_to is read. Unread is then
-- "created after the mark and not read one by one", which the audience index ranges over without
-- touching the older history, however long Operations' platform feed grows.
--
-- The mark is set a grace period behind the moment of marking (Notifications.MARK_GRACE), and
-- what the user can see inside that period gets ordinary read rows. A notification is stamped when
-- it is inserted but becomes visible only when its transaction commits, so one inserted just before
-- the click and committed just after has a created_at before the click that the user never saw: a
-- mark at the click would swallow it. A mark behind every transaction still open cannot. A
-- timestamp, not a transaction id or snapshot: those do not survive the pg_dump restore the
-- backup runbook uses.
CREATE TABLE notification_read_marks (
  user_id     uuid PRIMARY KEY REFERENCES users (id),
  read_up_to  timestamptz NOT NULL,
  marked_at   timestamptz NOT NULL
);

-- A mark only moves forward and stays its user's: a read is never unread.
CREATE FUNCTION notification_read_marks_forward() RETURNS trigger
  LANGUAGE plpgsql
  AS $$
  BEGIN
    IF NEW.user_id <> OLD.user_id OR NEW.read_up_to < OLD.read_up_to THEN
      RAISE EXCEPTION 'notification_read_marks: a mark only moves forward' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;
CREATE TRIGGER notification_read_marks_forward BEFORE UPDATE ON notification_read_marks
  FOR EACH ROW EXECUTE FUNCTION notification_read_marks_forward();

COMMENT ON TABLE notifications IS
  'In-app notifications addressed to a scope and roles; ids, integers and codes only. Append only for zawed_app.';
COMMENT ON TABLE notification_reads IS
  'Per-user read state of notifications: one row per user and notification read. Append only for zawed_app.';
COMMENT ON TABLE notification_read_marks IS
  'Per-user "read up to" mark: every notification created at or before read_up_to is read. Moves forward only.';

-- Append only for the application: a notification is never edited, and a read is never unread.
-- Neither table gets UPDATE or DELETE; the retention purge goes through purge_notifications below.
-- A mark is updated in place, forward only (trigger above), and never deleted.
GRANT SELECT, INSERT ON notifications TO zawed_app;
GRANT SELECT, INSERT ON notification_reads TO zawed_app;
GRANT SELECT, INSERT, UPDATE ON notification_read_marks TO zawed_app;

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
--
-- A platform session without a user is a POS device (a fueling, an offline sync, a shift) or a
-- job. What such a session causes is a posting crossing a low-balance threshold and a hard alert,
-- so those two types are all it may write (B7 W5 review, L1): a device cannot announce a top-up,
-- a payout or a ticket answer to anyone.
CREATE POLICY notifications_write ON notifications FOR INSERT
  WITH CHECK (
    app_scope_type() IS NOT NULL
    AND actor_id IS NOT DISTINCT FROM app_user_id()
    AND CASE
          WHEN app_is_platform() THEN
            app_user_id() IS NOT NULL OR type IN ('WALLET_LOW_BALANCE', 'HARD_ALERT_RAISED')
          ELSE
            app_user_id() IS NOT NULL
            AND (audience_scope_type = 'PLATFORM'
                 OR (audience_scope_type = app_scope_type() AND audience_scope_id = app_scope_id()))
        END);

-- Each user reads and writes only their own read state, and only for a notification they can see.
ALTER TABLE notification_reads ENABLE ROW LEVEL SECURITY;
CREATE POLICY notification_reads_own ON notification_reads FOR SELECT
  USING (user_id = (SELECT app_user_id()));
CREATE POLICY notification_reads_mark ON notification_reads FOR INSERT
  WITH CHECK (user_id = (SELECT app_user_id())
              AND EXISTS (SELECT 1 FROM notifications n WHERE n.id = notification_id));

-- A mark is its own user's only, to read, set and move.
ALTER TABLE notification_read_marks ENABLE ROW LEVEL SECURITY;
CREATE POLICY notification_read_marks_own ON notification_read_marks FOR SELECT
  USING (user_id = (SELECT app_user_id()));
CREATE POLICY notification_read_marks_set ON notification_read_marks FOR INSERT
  WITH CHECK (user_id = (SELECT app_user_id()));
CREATE POLICY notification_read_marks_move ON notification_read_marks FOR UPDATE
  USING (user_id = (SELECT app_user_id()))
  WITH CHECK (user_id = (SELECT app_user_id()));

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
