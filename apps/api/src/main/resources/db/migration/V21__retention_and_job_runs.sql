-- B7 W4: retention of the sign-in tables, and a record of every scheduled job run (ADR 0028).
--
--  1. Ended web sessions are purged a while after they end. A session row is one sign-in: it is
--     read on every request and kept, revoked or expired, for the record (V2). Nothing removed one,
--     so the table only grew. purge_sessions deletes, in bounded batches, rows that ended before the
--     caller's cutoff and refuses a cutoff inside 30 days, whoever calls it.
--  2. MFA challenges are purged shortly after they expire. A challenge lives 5 or 10 minutes and is
--     single use; an unfinished enrolment's row still holds the pending TOTP secret (encrypted).
--     purge_mfa_challenges refuses a cutoff inside one hour.
--  3. job_runs: one row per run of a scheduled job (platform.jobs.Jobs), so a job that silently
--     stopped is visible (the jobs health indicator, GET /api/v1/ops/jobs). Append only, written
--     through one checked function, kept 90 days by purge_job_runs (never under 30).
--
-- agent_sessions (a station agent's shift on a device, V7) is NOT touched by anything here: shifts
-- and the "last shift" period are built from it, and it is kept for ever (SessionRetentionTests).
--
-- The pattern is V14's purge_device_nonces and V17's purge_idempotency_keys: a bounded
-- SECURITY DEFINER function owned by the migration user, search_path pinned, revoked from PUBLIC and
-- executable by zawed_app, which keeps no DELETE grant on any of these tables.

-- ----------------------------------------------------------------------------------------------
-- 1. Sessions
-- ----------------------------------------------------------------------------------------------

-- When a session stopped being usable: revoked, idle-expired or past its absolute end, whichever
-- came first (Sessions.usableAt is the same rule). A live session's value is in the future, so no
-- cutoff in the past can reach it, however old the session is.
CREATE INDEX sessions_ended_at_idx
  ON sessions ((least(coalesce(revoked_at, 'infinity'::timestamptz), expires_at, absolute_expires_at)));

-- Deletes at most p_limit sessions that ended before p_before and says how many. The caller passes
-- its retention (90 days by default); anything inside 30 days is refused.
--
-- Concurrency with sign-in and refresh: the rows are chosen and locked with FOR UPDATE SKIP LOCKED,
-- so a purge never waits behind a request and never makes one wait on a row it is about to delete
-- (a live row is never chosen anyway). The ended-before test is repeated on the row being deleted:
-- with the lock already held it cannot have changed, so this is a second line, not the first.
CREATE FUNCTION purge_sessions(p_before timestamptz, p_limit integer) RETURNS integer
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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
REVOKE ALL ON FUNCTION purge_sessions(timestamptz, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION purge_sessions(timestamptz, integer) TO zawed_app;

COMMENT ON TABLE sessions IS
  'One row per web sign-in. Checked on every request. Revoked and expired rows are kept for the record, then '
  'purged zawed.sessions.retention after they end (90 days by default, never under 30; V21, ADR 0028). '
  'Not agent_sessions: those are kept.';

-- ----------------------------------------------------------------------------------------------
-- 2. MFA challenges
-- ----------------------------------------------------------------------------------------------

CREATE INDEX mfa_challenges_expires_at_idx ON mfa_challenges (expires_at);

-- Deletes at most p_limit challenges that expired before p_before. A challenge lives at most
-- 10 minutes (SignIn.ENROLL_CHALLENGE_LIFETIME) and its expiry never moves, so a cutoff an hour or
-- more in the past only reaches challenges nobody can use any more.
CREATE FUNCTION purge_mfa_challenges(p_before timestamptz, p_limit integer) RETURNS integer
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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
REVOKE ALL ON FUNCTION purge_mfa_challenges(timestamptz, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION purge_mfa_challenges(timestamptz, integer) TO zawed_app;

COMMENT ON TABLE mfa_challenges IS
  'Second sign-in step after a correct password. Single use, short-lived, bound to one user. Purged '
  'zawed.sessions.mfa-challenge-retention after expiry (1 day by default, never under 1 hour; V21).';

-- ----------------------------------------------------------------------------------------------
-- 3. Job runs
-- ----------------------------------------------------------------------------------------------

-- The only names a run's counts may use. A fixed list, not a pattern: a pattern of identifier keys
-- would still let a value (a phone number, a plate) be written as a key. platform.jobs.JobResult
-- holds the same list (COUNT_KEYS), and JobsTests checks the two agree; a new count is added to both.
CREATE FUNCTION job_run_count_keys() RETURNS text[]
  LANGUAGE sql IMMUTABLE SET search_path = public, pg_temp
  AS $$ SELECT ARRAY['columnErrors', 'deleted', 'failed', 'findings', 'mfaChallenges', 'payables', 'reencrypted',
                     'remaining', 'sessions', 'wallets']::text[] $$;

-- A run's counts are a flat object of non-negative integers under the keys above ({"sessions": 12}).
-- Never a value, a name or a message: no row data can be written here.
-- PL/pgSQL so the object test comes first for certain: jsonb_each refuses anything but an object.
CREATE FUNCTION job_run_counts_valid(p_counts jsonb) RETURNS boolean
  LANGUAGE plpgsql IMMUTABLE SET search_path = public, pg_temp
  AS $$
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
  $$;

CREATE TABLE job_runs (
  id           uuid PRIMARY KEY,
  job_name     text NOT NULL CHECK (job_name ~ '^[a-z][a-z0-9-]{0,62}$'),
  -- Which instance ran it ("pid@host"): enough to tell two replicas apart, nothing else.
  instance_id  text NOT NULL CHECK (instance_id ~ '^[A-Za-z0-9@._:-]{1,128}$'),
  started_at   timestamptz NOT NULL,
  finished_at  timestamptz NOT NULL,
  -- SKIPPED_LOCKED: the job's own lock or lease said another instance runs it (or already did).
  outcome      text NOT NULL CHECK (outcome IN ('SUCCESS', 'FAILED', 'SKIPPED_LOCKED')),
  counts       jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (job_run_counts_valid(counts)),
  -- The exception's class name only, never its message (a constraint error quotes the failing row).
  error_class  text CHECK (error_class ~ '^[A-Za-z_$][A-Za-z0-9_$.]{0,199}$'),
  CONSTRAINT job_runs_finished_after_start CHECK (finished_at >= started_at),
  CONSTRAINT job_runs_error_only_on_failure CHECK ((outcome = 'FAILED') = (error_class IS NOT NULL))
);

-- The health check's two questions per job: the latest run, and the latest success or failure.
CREATE INDEX job_runs_job_started_idx ON job_runs (job_name, started_at DESC, id DESC);
CREATE INDEX job_runs_job_outcome_finished_idx ON job_runs (job_name, outcome, finished_at DESC);
-- The purge.
CREATE INDEX job_runs_started_at_idx ON job_runs (started_at);

COMMENT ON TABLE job_runs IS
  'One row per run of a scheduled job: outcome, timings, numeric counts, error class. Append only, written by '
  'job_run_record; purged after 90 days (V21, ADR 0028).';

ALTER TABLE job_runs ENABLE ROW LEVEL SECURITY;
-- Operations' own record: readable in the platform scope only. No INSERT, UPDATE or DELETE policy or
-- grant: rows arrive through job_run_record and leave through purge_job_runs.
CREATE POLICY job_runs_platform_read ON job_runs FOR SELECT USING (app_is_platform());
GRANT SELECT ON job_runs TO zawed_app;

-- Records one finished run. SECURITY DEFINER because a scheduled job has no principal and so no
-- scope. The table's checks do the validation; a run may not claim to finish in the future, so a
-- forged success cannot hold a job's health green beyond the present.
CREATE FUNCTION job_run_record(p_id uuid, p_job text, p_instance text, p_started timestamptz,
                               p_finished timestamptz, p_outcome text, p_counts jsonb, p_error_class text)
  RETURNS void
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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
REVOKE ALL ON FUNCTION job_run_record(uuid, text, text, timestamptz, timestamptz, text, jsonb, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION job_run_record(uuid, text, text, timestamptz, timestamptz, text, jsonb, text) TO zawed_app;

-- Per job name asked for: the latest run of any outcome, the latest success and the latest failure.
-- SECURITY DEFINER so the health indicator, which has no principal, can answer; it returns names,
-- times, outcomes, numeric counts and class names only, none of them tenant data.
CREATE FUNCTION job_run_latest(p_names text[])
  RETURNS TABLE (job_name text, last_started_at timestamptz, last_finished_at timestamptz, last_outcome text,
                 last_success_at timestamptz, last_success_counts jsonb, last_failure_at timestamptz,
                 last_failure_error_class text)
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp
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
REVOKE ALL ON FUNCTION job_run_latest(text[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION job_run_latest(text[]) TO zawed_app;

-- Deletes at most p_limit runs that started before p_before; refuses anything inside 30 days.
CREATE FUNCTION purge_job_runs(p_before timestamptz, p_limit integer) RETURNS integer
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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
REVOKE ALL ON FUNCTION purge_job_runs(timestamptz, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION purge_job_runs(timestamptz, integer) TO zawed_app;
