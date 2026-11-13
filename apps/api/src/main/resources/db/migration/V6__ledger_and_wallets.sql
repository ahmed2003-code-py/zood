-- Ledger and wallets (phase B3): double-entry ledger in integer piastres, company wallets with a
-- cached balance, top-up requests, reconciliation runs and findings.
--
-- Rules from V1 and V4 apply. What this migration guarantees in the database, not in code
-- (implementation plan 3.5):
--   * one source (a top-up, later a fueling or a payout) posts at most once: (source_type, source_id)
--     is unique on ledger_transactions;
--   * every ledger transaction balances, debits equal credits, with at least two entries, checked by
--     a deferred constraint trigger at commit;
--   * ledger transactions and entries are immutable: the runtime role has no UPDATE or DELETE, and a
--     trigger refuses UPDATE, DELETE and TRUNCATE for every role, the owner included;
--   * a cached wallet balance never goes below zero (CHECK);
--   * row-level security: a platform scope sees everything, a company scope sees its own wallet,
--     entries and top-ups, an operator scope sees none of it (station payables arrive in B6).

-- -------------------------------------------------------------------------------------- accounts

CREATE TABLE ledger_accounts (
  id          uuid PRIMARY KEY,
  -- platform:bank | company:{id}:wallet | station:{id}:payable (reserved for B4/B6)
  code        text NOT NULL,
  kind        text NOT NULL CHECK (kind IN ('PLATFORM_BANK', 'COMPANY_WALLET', 'STATION_PAYABLE')),
  company_id  uuid REFERENCES companies (id),
  station_id  uuid REFERENCES stations (id),
  created_at  timestamptz NOT NULL,
  CONSTRAINT ledger_accounts_owner_matches_kind CHECK (
       (kind = 'PLATFORM_BANK' AND company_id IS NULL AND station_id IS NULL)
    OR (kind = 'COMPANY_WALLET' AND company_id IS NOT NULL AND station_id IS NULL)
    OR (kind = 'STATION_PAYABLE' AND company_id IS NULL AND station_id IS NOT NULL))
);

CREATE UNIQUE INDEX ledger_accounts_code_key ON ledger_accounts (code);
CREATE UNIQUE INDEX ledger_accounts_company_key ON ledger_accounts (company_id) WHERE company_id IS NOT NULL;
CREATE UNIQUE INDEX ledger_accounts_station_key ON ledger_accounts (station_id) WHERE station_id IS NOT NULL;

COMMENT ON TABLE ledger_accounts IS 'Named accounts of the double-entry ledger (plan 3.5). Amounts live on ledger_entries.';

-- ---------------------------------------------------------------------------------- transactions

CREATE TABLE ledger_transactions (
  id           uuid PRIMARY KEY,
  -- What caused the posting. The pair is unique: a top-up, a fueling or a payout can never post twice.
  source_type  text NOT NULL CHECK (source_type IN ('TOPUP', 'FUELING', 'PAYOUT', 'REVERSAL', 'ADJUSTMENT')),
  source_id    uuid NOT NULL,
  -- The company whose wallet this transaction touches, if any. Lets the policy avoid a join.
  company_id   uuid REFERENCES companies (id),
  -- A bank reference, a receipt reference: what a reader would look for on a statement.
  reference    text,
  description  text,
  posted_at    timestamptz NOT NULL,
  posted_by    uuid
);

CREATE UNIQUE INDEX ledger_transactions_source_key ON ledger_transactions (source_type, source_id);
CREATE INDEX ledger_transactions_company_id_idx ON ledger_transactions (company_id, id);

COMMENT ON TABLE ledger_transactions IS 'One balanced posting. Immutable: a correction is a new transaction (REVERSAL or ADJUSTMENT).';

-- --------------------------------------------------------------------------------------- entries

CREATE TABLE ledger_entries (
  id                      uuid PRIMARY KEY,
  -- Insert order. Entries of one wallet are inserted under that wallet's row lock, so for one
  -- wallet this sequence is the exact order of its running balance, whatever the clocks of two
  -- API instances say. The statement orders and pages by it.
  seq                     bigint GENERATED ALWAYS AS IDENTITY,
  transaction_id          uuid NOT NULL REFERENCES ledger_transactions (id),
  account_id              uuid NOT NULL REFERENCES ledger_accounts (id),
  -- The account's company, copied here so the policy needs no join; checked by a trigger.
  company_id              uuid REFERENCES companies (id),
  direction               text NOT NULL CHECK (direction IN ('DEBIT', 'CREDIT')),
  amount_piastres         bigint NOT NULL CHECK (amount_piastres > 0),
  -- The wallet's cached balance right after this entry (wallet accounts only, PRD 8.3).
  balance_after_piastres  bigint CHECK (balance_after_piastres >= 0),
  posted_at               timestamptz NOT NULL
);

CREATE UNIQUE INDEX ledger_entries_seq_key ON ledger_entries (seq);
CREATE INDEX ledger_entries_transaction_id_idx ON ledger_entries (transaction_id);
CREATE INDEX ledger_entries_account_seq_idx ON ledger_entries (account_id, seq DESC);
CREATE INDEX ledger_entries_company_seq_idx ON ledger_entries (company_id, seq DESC);

COMMENT ON TABLE ledger_entries IS 'One side of a posting, in integer piastres. Append only, for every role.';

-- --------------------------------------------------------------------------------------- wallets

CREATE TABLE company_wallets (
  company_id        uuid PRIMARY KEY REFERENCES companies (id),
  account_id        uuid NOT NULL REFERENCES ledger_accounts (id),
  -- Derived from the ledger, changed only under this row's lock in the same transaction as the
  -- entry that changes it; reconciled nightly against the entries (PRD 4.2). Never negative.
  balance_piastres  bigint NOT NULL DEFAULT 0 CHECK (balance_piastres >= 0),
  created_at        timestamptz NOT NULL,
  updated_at        timestamptz NOT NULL
);

CREATE UNIQUE INDEX company_wallets_account_key ON company_wallets (account_id);

COMMENT ON TABLE company_wallets IS 'The row the fueling engine locks first (plan 3.4 step 4). The ledger is the truth; this is its cache.';

-- -------------------------------------------------------------------------------------- top-ups

CREATE TABLE topup_requests (
  id                     uuid PRIMARY KEY,
  company_id             uuid NOT NULL REFERENCES companies (id),
  amount_piastres        bigint NOT NULL CHECK (amount_piastres > 0),
  -- As typed, for display; and normalised (trimmed, upper case, no spaces) for the uniqueness rule.
  bank_reference         text NOT NULL,
  bank_reference_key     text NOT NULL CHECK (bank_reference_key <> ''),
  transfer_date          date NOT NULL,
  note                   text,
  status                 text NOT NULL CHECK (status IN ('PENDING', 'CONFIRMED', 'REJECTED', 'CANCELLED')),
  -- NULL when Operations credited the wallet directly, with no request from the company (PRD 6.3).
  requested_by           uuid,
  requested_at           timestamptz NOT NULL,
  decided_by             uuid,
  decided_at             timestamptz,
  decision_reason        text,
  ledger_transaction_id  uuid REFERENCES ledger_transactions (id),
  created_at             timestamptz NOT NULL,
  updated_at             timestamptz NOT NULL,
  -- A confirmed top-up is exactly one that posted; nothing else may point at a posting.
  CONSTRAINT topup_requests_confirmed_has_posting CHECK ((status = 'CONFIRMED') = (ledger_transaction_id IS NOT NULL)),
  CONSTRAINT topup_requests_rejection_has_reason CHECK (status <> 'REJECTED' OR decision_reason IS NOT NULL),
  CONSTRAINT topup_requests_decided CHECK ((status = 'PENDING') = (decided_at IS NULL))
);

-- One bank transfer credits once, platform-wide: a reference belongs to at most one live request.
-- A rejected or cancelled request frees its reference.
CREATE UNIQUE INDEX topup_requests_reference_live_key ON topup_requests (bank_reference_key)
  WHERE status IN ('PENDING', 'CONFIRMED');
CREATE UNIQUE INDEX topup_requests_ledger_transaction_key ON topup_requests (ledger_transaction_id)
  WHERE ledger_transaction_id IS NOT NULL;
CREATE INDEX topup_requests_company_id_idx ON topup_requests (company_id, id);
CREATE INDEX topup_requests_status_idx ON topup_requests (status, id);

COMMENT ON TABLE topup_requests IS 'Top-up by bank transfer (PRD 4.6): requested by the company, confirmed or rejected by Operations.';

-- ------------------------------------------------------------------------------- reconciliation

CREATE TABLE reconciliation_runs (
  id               uuid PRIMARY KEY,
  trigger          text NOT NULL CHECK (trigger IN ('SCHEDULED', 'MANUAL')),
  triggered_by     uuid,
  started_at       timestamptz NOT NULL,
  finished_at      timestamptz,
  wallets_checked  integer NOT NULL DEFAULT 0,
  findings_count   integer NOT NULL DEFAULT 0
);

CREATE INDEX reconciliation_runs_started_at_idx ON reconciliation_runs (started_at);

CREATE TABLE reconciliation_findings (
  id                       uuid PRIMARY KEY,
  run_id                   uuid NOT NULL REFERENCES reconciliation_runs (id),
  company_id               uuid NOT NULL REFERENCES companies (id),
  kind                     text NOT NULL CHECK (kind IN ('BALANCE_MISMATCH', 'MISSING_WALLET')),
  cached_balance_piastres  bigint,
  ledger_balance_piastres  bigint,
  difference_piastres      bigint,
  created_at               timestamptz NOT NULL
);

CREATE INDEX reconciliation_findings_run_id_idx ON reconciliation_findings (run_id, id);
CREATE INDEX reconciliation_findings_company_id_idx ON reconciliation_findings (company_id);

COMMENT ON TABLE reconciliation_findings IS 'Output of the nightly comparison of every cached wallet balance with its ledger (PRD 4.2, 8.3).';

-- ------------------------------------------------------------------- integrity, by the database

-- Debits equal credits, with at least two entries, checked when the transaction commits (the
-- entries are inserted one statement at a time, so an immediate check could never pass).
CREATE FUNCTION ledger_check_balanced() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
  AS $$
  DECLARE
    v_txn     uuid;
    v_debits  bigint;
    v_credits bigint;
    v_count   integer;
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
    RETURN NULL;
  END
  $$;

REVOKE ALL ON FUNCTION ledger_check_balanced() FROM PUBLIC;

-- On the transaction, so a posting with no entries is caught; on the entries, so an entry added to
-- an old transaction is caught too.
CREATE CONSTRAINT TRIGGER ledger_transactions_balanced AFTER INSERT ON ledger_transactions
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ledger_check_balanced();
CREATE CONSTRAINT TRIGGER ledger_entries_balanced AFTER INSERT ON ledger_entries
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ledger_check_balanced();

-- An entry's company is its account's company, so the policy below can trust the copied column.
CREATE FUNCTION ledger_entry_check_account() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
  AS $$
  DECLARE
    v_company uuid;
    v_found   boolean;
  BEGIN
    SELECT true, company_id INTO v_found, v_company FROM ledger_accounts WHERE id = NEW.account_id;
    IF v_found IS NOT TRUE THEN
      RAISE EXCEPTION 'ledger account % does not exist', NEW.account_id USING ERRCODE = 'foreign_key_violation';
    END IF;
    IF v_company IS DISTINCT FROM NEW.company_id THEN
      RAISE EXCEPTION 'ledger entry company % does not match account %', NEW.company_id, NEW.account_id
        USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;

REVOKE ALL ON FUNCTION ledger_entry_check_account() FROM PUBLIC;

CREATE TRIGGER ledger_entries_account BEFORE INSERT ON ledger_entries
  FOR EACH ROW EXECUTE FUNCTION ledger_entry_check_account();

-- Immutable for every role, the owner included. A correction is a new transaction.
CREATE FUNCTION ledger_refuse_change() RETURNS trigger
  LANGUAGE plpgsql
  AS $$
  BEGIN
    RAISE EXCEPTION '% is immutable: % is not allowed', TG_TABLE_NAME, TG_OP
      USING ERRCODE = 'insufficient_privilege';
  END
  $$;

CREATE TRIGGER ledger_entries_immutable BEFORE UPDATE OR DELETE ON ledger_entries
  FOR EACH ROW EXECUTE FUNCTION ledger_refuse_change();
CREATE TRIGGER ledger_entries_no_truncate BEFORE TRUNCATE ON ledger_entries
  FOR EACH STATEMENT EXECUTE FUNCTION ledger_refuse_change();
CREATE TRIGGER ledger_transactions_immutable BEFORE UPDATE OR DELETE ON ledger_transactions
  FOR EACH ROW EXECUTE FUNCTION ledger_refuse_change();
CREATE TRIGGER ledger_transactions_no_truncate BEFORE TRUNCATE ON ledger_transactions
  FOR EACH STATEMENT EXECUTE FUNCTION ledger_refuse_change();

-- --------------------------------------------------------------------------- reconciliation job

-- Compares every cached wallet balance with the sum of its entries and records a run with its
-- findings. SECURITY DEFINER: the nightly job has no principal and so no scope, and the comparison
-- must see every tenant. It takes an advisory lock for the length of its transaction, so two API
-- instances never run it at once; returns no row when another run holds the lock.
--
-- The comparison is one statement, so it reads one snapshot: a posting that commits meanwhile is
-- either wholly in (wallet and entries) or wholly out, and can never show as a mismatch.
CREATE FUNCTION ledger_reconcile(p_run_id uuid, p_trigger text, p_actor uuid, p_actor_role text, p_now timestamptz,
                                 p_request_id text)
  RETURNS SETOF reconciliation_runs
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
  AS $$
  DECLARE
    v_wallets  integer;
    v_findings integer;
  BEGIN
    IF NOT pg_try_advisory_xact_lock(hashtext('ledger_reconcile')) THEN
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
    GET DIAGNOSTICS v_findings = ROW_COUNT;
    SELECT count(*) INTO v_wallets FROM company_wallets;
    UPDATE reconciliation_runs
       SET finished_at = clock_timestamp(), wallets_checked = v_wallets, findings_count = v_findings
     WHERE id = p_run_id;
    INSERT INTO audit_log (id, occurred_at, actor_id, actor_role, action, entity_type, entity_id,
                           scope_type, scope_id, metadata, request_id, ip_prefix)
      VALUES (uuidv7(), p_now, p_actor, p_actor_role, 'RECONCILIATION_RUN', 'RECONCILIATION_RUN', p_run_id,
              'PLATFORM', NULL,
              jsonb_build_object('trigger', p_trigger, 'walletsChecked', v_wallets, 'findingsCount', v_findings),
              p_request_id, NULL);
    RETURN QUERY SELECT * FROM reconciliation_runs WHERE id = p_run_id;
  END
  $$;

REVOKE ALL ON FUNCTION ledger_reconcile(uuid, text, uuid, text, timestamptz, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ledger_reconcile(uuid, text, uuid, text, timestamptz, text) TO zawed_app;

-- ------------------------------------------------------------------------------------- backfill

-- The platform's bank account, and a wallet for every company that already exists. New companies
-- get theirs from the ledger module when tenancy announces the company (CompanyCreated).
INSERT INTO ledger_accounts (id, code, kind, company_id, station_id, created_at)
  VALUES (uuidv7(), 'platform:bank', 'PLATFORM_BANK', NULL, NULL, now());

INSERT INTO ledger_accounts (id, code, kind, company_id, station_id, created_at)
  SELECT uuidv7(), 'company:' || c.id || ':wallet', 'COMPANY_WALLET', c.id, NULL, now()
    FROM companies c;

INSERT INTO company_wallets (company_id, account_id, balance_piastres, created_at, updated_at)
  SELECT a.company_id, a.id, 0, now(), now()
    FROM ledger_accounts a WHERE a.kind = 'COMPANY_WALLET';

-- ----------------------------------------------------------------------------- grants and RLS

GRANT SELECT, INSERT ON ledger_accounts, ledger_transactions, ledger_entries TO zawed_app;  -- append only
GRANT SELECT, INSERT, UPDATE ON company_wallets TO zawed_app;                              -- the cached balance
GRANT SELECT, INSERT, UPDATE ON topup_requests TO zawed_app;                               -- status transitions
GRANT SELECT ON reconciliation_runs, reconciliation_findings TO zawed_app;                 -- written by ledger_reconcile

ALTER TABLE ledger_accounts ENABLE ROW LEVEL SECURITY;
CREATE POLICY ledger_accounts_by_scope ON ledger_accounts
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id()))
  WITH CHECK (app_is_platform());

ALTER TABLE ledger_transactions ENABLE ROW LEVEL SECURITY;
CREATE POLICY ledger_transactions_by_scope ON ledger_transactions
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id()))
  WITH CHECK (app_is_platform());

ALTER TABLE ledger_entries ENABLE ROW LEVEL SECURITY;
CREATE POLICY ledger_entries_by_scope ON ledger_entries
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id()))
  WITH CHECK (app_is_platform());

ALTER TABLE company_wallets ENABLE ROW LEVEL SECURITY;
CREATE POLICY company_wallets_by_scope ON company_wallets
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id()))
  WITH CHECK (app_is_platform());

-- A company reads its own requests, may create them only as PENDING, and may change them only
-- to CANCELLED. Confirming (which posts money) and rejecting are platform actions.
ALTER TABLE topup_requests ENABLE ROW LEVEL SECURITY;
CREATE POLICY topup_requests_read ON topup_requests FOR SELECT
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id()));
CREATE POLICY topup_requests_request ON topup_requests FOR INSERT
  WITH CHECK (app_is_platform()
              OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id() AND status = 'PENDING'));
CREATE POLICY topup_requests_decide ON topup_requests FOR UPDATE
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id()))
  WITH CHECK (app_is_platform()
              OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id() AND status = 'CANCELLED'));

ALTER TABLE reconciliation_runs ENABLE ROW LEVEL SECURITY;
CREATE POLICY reconciliation_runs_platform ON reconciliation_runs FOR SELECT USING (app_is_platform());

ALTER TABLE reconciliation_findings ENABLE ROW LEVEL SECURITY;
CREATE POLICY reconciliation_findings_platform ON reconciliation_findings FOR SELECT USING (app_is_platform());
