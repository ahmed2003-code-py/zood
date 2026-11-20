-- U1: demo-request leads from the public landing page, and the shared rate-limit counters the
-- public form needs (u1-leads-contract.md, ADR 0012 amendment).
--
--  1. rate_limit_counters: fixed-window counters in PostgreSQL, so a limit survives a deploy and holds
--     across instances (the in-memory limiter resets on every deploy and counts per instance). The
--     runtime role has no grant on the table: it counts through rate_limit_hit and nothing else, and
--     expired windows go through purge_rate_limit_counters (the reviewed purge_idempotency_keys
--     pattern: bounded, SECURITY DEFINER, revoked from PUBLIC).
--  2. leads: personal data of prospects (a name, a phone, an e-mail). Platform users only. The public
--     form has no principal and no scope, so it writes through submit_lead, a SECURITY DEFINER
--     function with fixed columns; the runtime role gets no INSERT, and UPDATE on the Ops columns
--     only. A POS route runs in the platform scope with no user (DbScope.runAs(PLATFORM, null)), so
--     the policies also ask for a user: no device path can read a lead.
--  3. purge_closed_leads: closed leads are deleted after their retention (24 months by default;
--     LeadRetention), never inside 90 days of closing.

-- ----------------------------------------------------------------------------------------------
-- 1. Shared rate-limit counters
-- ----------------------------------------------------------------------------------------------

CREATE TABLE rate_limit_counters (
  bucket        text NOT NULL CHECK (bucket ~ '^[a-z][a-z0-9-]{1,63}$'),
  -- A keyed hash (LookupHasher) of the address or contact: the table is never a list of IPs or phones.
  key_hash      text NOT NULL CHECK (key_hash ~ '^[0-9a-f]{64}$'),
  window_start  timestamptz NOT NULL,
  expires_at    timestamptz NOT NULL CHECK (expires_at > window_start),
  hits          integer NOT NULL CHECK (hits >= 1),
  PRIMARY KEY (bucket, key_hash, window_start)
);

CREATE INDEX rate_limit_counters_expires_at_idx ON rate_limit_counters (expires_at);

COMMENT ON TABLE rate_limit_counters IS
  'Fixed-window request counters shared by every instance (ADR 0012, V22). Written only by rate_limit_hit, '
  'removed only by purge_rate_limit_counters; the runtime role has no grant on the table.';

-- No grant to zawed_app and no policy: row-level security on, so even a later accidental grant shows
-- no rows.
ALTER TABLE rate_limit_counters ENABLE ROW LEVEL SECURITY;

-- Counts one attempt in the current window of p_window_seconds (aligned to the epoch, so every
-- instance agrees where a window starts) and answers the count so far and the seconds until the
-- window ends. The database clock decides, not the caller's. One upsert: two instances counting the
-- same key at the same moment wait on the row and both counts land.
CREATE FUNCTION rate_limit_hit(p_bucket text, p_key_hash text, p_window_seconds integer)
  RETURNS TABLE (hit_count integer, retry_after_seconds integer)
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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
REVOKE ALL ON FUNCTION rate_limit_hit(text, text, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION rate_limit_hit(text, text, integer) TO zawed_app;

-- Deletes at most p_limit windows that have ended. Nothing else: a window still running cannot be
-- removed through it, whoever calls it, so it is no way to reset a limit.
CREATE FUNCTION purge_rate_limit_counters(p_limit integer) RETURNS integer
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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
REVOKE ALL ON FUNCTION purge_rate_limit_counters(integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION purge_rate_limit_counters(integer) TO zawed_app;

-- ----------------------------------------------------------------------------------------------
-- 2. Leads
-- ----------------------------------------------------------------------------------------------

-- A reference Ops can say on the phone: L-10001, L-10002...
CREATE SEQUENCE lead_reference_seq START WITH 10001;

CREATE TABLE leads (
  id                 uuid PRIMARY KEY,
  reference          text NOT NULL UNIQUE CHECK (reference ~ '^L-[0-9]{5,}$'),
  company_name       text NOT NULL CHECK (char_length(company_name) BETWEEN 2 AND 120),
  contact_name       text NOT NULL CHECK (char_length(contact_name) BETWEEN 2 AND 80),
  -- An Egyptian mobile number, normalised by the API: 01, then 0, 1, 2 or 5, then eight digits.
  phone              text NOT NULL CHECK (phone ~ '^01[0125][0-9]{8}$'),
  -- Lower-cased by the API, so a repeat with other capitals is the same contact.
  email              text NOT NULL CHECK (char_length(email) BETWEEN 3 AND 254 AND email = lower(email)
                                          AND email ~ '^[^@[:space:]]+@[^@[:space:]]+$'),
  fleet_size         text NOT NULL CHECK (fleet_size IN ('1-19', '20-49', '50-99', '100-249', '250+')),
  locale             text NOT NULL CHECK (locale IN ('ar', 'en')),
  status             text NOT NULL DEFAULT 'NEW' CHECK (status IN ('NEW', 'CONTACTED', 'QUALIFIED', 'CLOSED')),
  note               text CHECK (note IS NULL OR char_length(note) BETWEEN 1 AND 1000),
  -- A repeat from the same phone or e-mail within 24 hours counts here instead of making a new lead.
  request_count      integer NOT NULL DEFAULT 1 CHECK (request_count >= 1),
  last_requested_at  timestamptz NOT NULL,
  created_at         timestamptz NOT NULL,
  updated_at         timestamptz NOT NULL,
  updated_by         uuid,
  -- When the lead was last closed: the retention clock. Cleared when Ops reopens it.
  closed_at          timestamptz,
  CHECK ((status = 'CLOSED') = (closed_at IS NOT NULL))
);

-- The Ops list: newest first, keyset on (created_at, id), with or without a status.
CREATE INDEX leads_created_idx ON leads (created_at DESC, id DESC);
CREATE INDEX leads_status_created_idx ON leads (status, created_at DESC, id DESC);
-- The repeat check of submit_lead.
CREATE INDEX leads_phone_idx ON leads (phone, last_requested_at DESC);
CREATE INDEX leads_email_idx ON leads (email, last_requested_at DESC);
-- The retention purge.
CREATE INDEX leads_closed_idx ON leads (closed_at) WHERE status = 'CLOSED';

COMMENT ON TABLE leads IS
  'Demo requests from the public landing page (U1). Personal data: platform users only; inserted only by '
  'submit_lead; closed leads purged after their retention by purge_closed_leads (V22).';

-- Read and work the list; no INSERT (submit_lead), no DELETE (purge_closed_leads), and UPDATE only on
-- what Ops changes. What the prospect sent is never changed through the runtime role.
GRANT SELECT ON leads TO zawed_app;
GRANT UPDATE (status, note, updated_at, updated_by, closed_at) ON leads TO zawed_app;

ALTER TABLE leads ENABLE ROW LEVEL SECURITY;

-- A platform user. The platform scope alone is not enough: every POS route runs in it with no user.
CREATE POLICY leads_platform_read ON leads FOR SELECT
  USING (app_is_platform() AND app_user_id() IS NOT NULL);

-- The same, and the change is written in the acting user's name.
CREATE POLICY leads_platform_update ON leads FOR UPDATE
  USING (app_is_platform() AND app_user_id() IS NOT NULL)
  WITH CHECK (app_is_platform() AND app_user_id() IS NOT NULL AND updated_by = app_user_id());

-- The public form's only door. The arguments are the normalised values the API has validated; the
-- table's checks hold them again. A repeat (the same phone or e-mail, last requested within 24 hours)
-- counts on the existing lead and creates nothing, so the form answers alike either way. Submissions
-- are serialised by one transaction-level advisory lock: two identical requests at the same moment
-- cannot both miss the other and make two leads (the form is rate limited, so the lock is never hot).
-- Returns the lead's id and reference for the log line, and whether it was a repeat.
CREATE FUNCTION submit_lead(p_id uuid, p_company_name text, p_contact_name text, p_phone text, p_email text,
    p_fleet_size text, p_locale text)
  RETURNS TABLE (lead_id uuid, lead_reference text, repeated boolean)
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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
      UPDATE leads l SET last_requested_at = v_now, request_count = l.request_count + 1 WHERE l.id = v_id;
      lead_id := v_id;
      lead_reference := v_reference;
      repeated := true;
      RETURN NEXT;
      RETURN;
    END IF;
    v_reference := 'L-' || nextval('lead_reference_seq');
    INSERT INTO leads (id, reference, company_name, contact_name, phone, email, fleet_size, locale, status, note,
                       request_count, last_requested_at, created_at, updated_at, updated_by, closed_at)
      VALUES (p_id, v_reference, p_company_name, p_contact_name, p_phone, p_email, p_fleet_size, p_locale, 'NEW',
              NULL, 1, v_now, v_now, v_now, NULL, NULL);
    lead_id := p_id;
    lead_reference := v_reference;
    repeated := false;
    RETURN NEXT;
  END
  $$;
REVOKE ALL ON FUNCTION submit_lead(uuid, text, text, text, text, text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION submit_lead(uuid, text, text, text, text, text, text) TO zawed_app;

-- ----------------------------------------------------------------------------------------------
-- 3. Retention of closed leads
-- ----------------------------------------------------------------------------------------------

-- Deletes at most p_limit leads closed before p_before. Only closed leads: an open lead is Ops' work
-- in progress. The caller passes its retention (24 months by default); the function refuses anything
-- inside 90 days of closing, whoever calls it, so a mistaken setting cannot wipe the recent ones.
-- The audit rows stay: they carry the lead's id and reference, never its personal data.
CREATE FUNCTION purge_closed_leads(p_before timestamptz, p_limit integer) RETURNS integer
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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
REVOKE ALL ON FUNCTION purge_closed_leads(timestamptz, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION purge_closed_leads(timestamptz, integer) TO zawed_app;
