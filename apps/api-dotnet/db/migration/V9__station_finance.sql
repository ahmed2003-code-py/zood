-- B6: station finance (docs/architecture/b6-station-finance-contract.md, ADR 0020).
--
--  1. Station payables get what company wallets have had since V6: a cached balance changed only
--     under its row lock, in the same transaction as the entry that moves it, and recorded on the
--     entry. Unlike a wallet it may go below zero: a fueling reversed after it was paid out leaves
--     the station owing the platform, recovered from its next payout.
--  2. A reversal names the transaction it mirrors; the database checks the mirror and allows one
--     reversal per transaction.
--  3. Reconciliation compares station payables with their ledger too.
--  4. Station bank accounts under change control, payouts with dual control, releases of held
--     payables, two-person fueling reversals.
--  5. Row-level security: the platform writes; an operator reads its own stations' payable, its
--     entries, posted payouts, bank account and approved reversals; a company reads its own
--     approved reversals.
--  6. Station managers now sign in with a second factor (ADR 0009): sessions opened without one end.
--
-- Rules from V1, V3 and V4 apply. Dual control is a database rule here, not only an API rule: the
-- person who decides is never the person who asked, and is the user the transaction runs for.

-- ----------------------------------------------------------------------------------------------
-- 1. Station payables
-- ----------------------------------------------------------------------------------------------

CREATE TABLE station_payables (
  station_id        uuid PRIMARY KEY REFERENCES stations (id),
  -- The station's operator, copied here so the policy needs no join (a station never changes operator).
  operator_id       uuid NOT NULL REFERENCES station_operators (id),
  account_id        uuid NOT NULL REFERENCES ledger_accounts (id),
  -- What the platform owes the station: credits minus debits of its payable account. Derived from
  -- the ledger, changed only under this row's lock in the same transaction as the entry that
  -- changes it, reconciled nightly. No floor: below zero the station owes the platform.
  balance_piastres  bigint NOT NULL DEFAULT 0,
  created_at        timestamptz NOT NULL,
  updated_at        timestamptz NOT NULL
);

CREATE UNIQUE INDEX station_payables_account_key ON station_payables (account_id);
CREATE INDEX station_payables_operator_id_idx ON station_payables (operator_id);

COMMENT ON TABLE station_payables IS 'The row every posting to a station payable locks (after any wallet). The ledger is the truth; this is its cache.';

-- Payable accounts opened by B4 and B5 fuelings: their balance is the sum of their entries.
INSERT INTO station_payables (station_id, operator_id, account_id, balance_piastres, created_at, updated_at)
  SELECT a.station_id, s.operator_id, a.id,
         coalesce((SELECT sum(CASE e.direction WHEN 'CREDIT' THEN e.amount_piastres ELSE -e.amount_piastres END)
                     FROM ledger_entries e WHERE e.account_id = a.id), 0),
         a.created_at, now()
    FROM ledger_accounts a
    JOIN stations s ON s.id = a.station_id
   WHERE a.kind = 'STATION_PAYABLE';

-- A row of station finance belongs to the operator that runs its station, because row-level
-- security reads operator_id.
CREATE FUNCTION station_finance_same_operator() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
  AS $$
  BEGIN
    IF NOT EXISTS (SELECT 1 FROM stations WHERE id = NEW.station_id AND operator_id = NEW.operator_id) THEN
      RAISE EXCEPTION '%: station % is not run by operator %', TG_TABLE_NAME, NEW.station_id, NEW.operator_id
        USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;

REVOKE ALL ON FUNCTION station_finance_same_operator() FROM PUBLIC;

CREATE TRIGGER station_payables_same_operator BEFORE INSERT ON station_payables
  FOR EACH ROW EXECUTE FUNCTION station_finance_same_operator();

-- The running balance on an entry. V6 said "never negative" for every entry with a CHECK; that is
-- the wallet's rule, and a station payable may go below zero. The rule moves into the trigger that
-- already reads the entry's account, and gains the payable's own:
--   * a wallet (or any other) entry: the balance it records is never negative, as before;
--   * a station payable entry: it must record a balance, and that balance is the cached one its
--     transaction has just set. An entry can therefore never be added to a payable without moving
--     the cache to exactly the recorded value, by any code path, old or new.
ALTER TABLE ledger_entries DROP CONSTRAINT ledger_entries_balance_after_piastres_check;

CREATE OR REPLACE FUNCTION ledger_entry_check_account() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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

COMMENT ON COLUMN ledger_entries.balance_after_piastres IS
  'The cached balance right after this entry: a company wallet (never negative) or a station payable (any sign, V9). NULL on other accounts and on payable entries written before V9.';

-- ----------------------------------------------------------------------------------------------
-- 2. Reversals in the ledger
-- ----------------------------------------------------------------------------------------------

ALTER TABLE ledger_transactions ADD COLUMN reverses_transaction_id uuid REFERENCES ledger_transactions (id);
ALTER TABLE ledger_transactions ADD CONSTRAINT ledger_transactions_reversal_names_original
  CHECK ((source_type = 'REVERSAL') = (reverses_transaction_id IS NOT NULL));
ALTER TABLE ledger_transactions ADD CONSTRAINT ledger_transactions_not_its_own_reversal
  CHECK (reverses_transaction_id <> id);
-- A transaction is reversed at most once, whoever asks and however often.
CREATE UNIQUE INDEX ledger_transactions_reversed_once ON ledger_transactions (reverses_transaction_id)
  WHERE reverses_transaction_id IS NOT NULL;

-- Checked at commit with the balance (V6): a reversal and the transaction it reverses cancel out on
-- every account they touch, and a reversal is never itself reversed. Asked from either side, so an
-- entry added later to either transaction is caught too.
CREATE OR REPLACE FUNCTION ledger_check_balanced() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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

-- ----------------------------------------------------------------------------------------------
-- 3. Reconciliation covers station payables
-- ----------------------------------------------------------------------------------------------

ALTER TABLE reconciliation_runs ADD COLUMN payables_checked integer NOT NULL DEFAULT 0;

ALTER TABLE reconciliation_findings ALTER COLUMN company_id DROP NOT NULL;
ALTER TABLE reconciliation_findings ADD COLUMN station_id uuid REFERENCES stations (id);
CREATE INDEX reconciliation_findings_station_id_idx ON reconciliation_findings (station_id);
ALTER TABLE reconciliation_findings DROP CONSTRAINT reconciliation_findings_kind_check;
ALTER TABLE reconciliation_findings ADD CONSTRAINT reconciliation_findings_kind_check
  CHECK (kind IN ('BALANCE_MISMATCH', 'MISSING_WALLET', 'PAYABLE_MISMATCH', 'MISSING_PAYABLE'));
-- A finding is about one wallet or one payable.
ALTER TABLE reconciliation_findings ADD CONSTRAINT reconciliation_findings_subject CHECK (
     (kind IN ('BALANCE_MISMATCH', 'MISSING_WALLET') AND company_id IS NOT NULL AND station_id IS NULL)
  OR (kind IN ('PAYABLE_MISMATCH', 'MISSING_PAYABLE') AND company_id IS NULL AND station_id IS NOT NULL));

-- As V6_1, plus the same comparison for station payables: each cached payable balance against the
-- sum of its account's entries, in one statement (one snapshot), and a payable account that has no
-- cached row at all.
CREATE OR REPLACE FUNCTION ledger_reconcile(p_run_id uuid, p_trigger text, p_actor uuid, p_actor_role text,
                                            p_now timestamptz, p_request_id text)
  RETURNS SETOF reconciliation_runs
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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

-- ----------------------------------------------------------------------------------------------
-- 4a. Station bank accounts under change control (roadmap delta A3)
-- ----------------------------------------------------------------------------------------------

-- One row per requested account. PENDING until a second person decides; ACTIVE is the station's one
-- verified account; SUPERSEDED once a later one replaces it. Approval starts a payout hold.
CREATE TABLE station_bank_accounts (
  id                     uuid PRIMARY KEY,
  station_id             uuid NOT NULL REFERENCES stations (id),
  operator_id            uuid NOT NULL REFERENCES station_operators (id),
  account_kind           text NOT NULL CHECK (account_kind IN ('IBAN', 'ACCOUNT_NUMBER')),
  -- The IBAN or account number, AES-256-GCM bound to this row (platform.crypto.DataEncryptor).
  -- Never stored, returned, audited or logged in clear; the last four characters are what is shown.
  account_encrypted      bytea NOT NULL,
  account_last4          text NOT NULL CHECK (account_last4 ~ '^[A-Z0-9]{4}$'),
  bank_name              text NOT NULL,
  holder_name            text NOT NULL,
  -- The reference of the bank's letter that confirms the account belongs to the holder.
  bank_letter_reference  text NOT NULL,
  status                 text NOT NULL CHECK (status IN ('PENDING', 'ACTIVE', 'REJECTED', 'SUPERSEDED')),
  requested_by           uuid NOT NULL,
  requested_at           timestamptz NOT NULL,
  decided_by             uuid,
  decided_at             timestamptz,
  decision_reason        text,
  -- No payout may go to the station before this instant (set when the change is approved).
  payouts_held_until     timestamptz,
  superseded_at          timestamptz,
  created_at             timestamptz NOT NULL,
  updated_at             timestamptz NOT NULL,
  CONSTRAINT station_bank_accounts_decided CHECK ((status = 'PENDING') = (decided_at IS NULL)),
  CONSTRAINT station_bank_accounts_decider CHECK ((decided_at IS NULL) = (decided_by IS NULL)),
  -- Two-person rule: an account that was ever verified was approved by someone other than its requester.
  CONSTRAINT station_bank_accounts_two_person CHECK (status NOT IN ('ACTIVE', 'SUPERSEDED') OR decided_by <> requested_by),
  CONSTRAINT station_bank_accounts_rejection_has_reason CHECK (status <> 'REJECTED' OR decision_reason IS NOT NULL),
  CONSTRAINT station_bank_accounts_approval_starts_hold CHECK ((status IN ('ACTIVE', 'SUPERSEDED')) = (payouts_held_until IS NOT NULL)),
  CONSTRAINT station_bank_accounts_superseded CHECK ((status = 'SUPERSEDED') = (superseded_at IS NOT NULL))
);

CREATE UNIQUE INDEX station_bank_accounts_one_active ON station_bank_accounts (station_id) WHERE status = 'ACTIVE';
CREATE UNIQUE INDEX station_bank_accounts_one_pending ON station_bank_accounts (station_id) WHERE status = 'PENDING';
CREATE INDEX station_bank_accounts_station_id_idx ON station_bank_accounts (station_id, id);
CREATE INDEX station_bank_accounts_operator_id_idx ON station_bank_accounts (operator_id, id);
CREATE INDEX station_bank_accounts_status_idx ON station_bank_accounts (status, id);

COMMENT ON TABLE station_bank_accounts IS 'Where a station is paid (A3). A change is requested by one person and approved by another; approval holds payouts.';

CREATE FUNCTION station_bank_accounts_check_insert() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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

-- Every role, the owner included: only PENDING -> ACTIVE or REJECTED (decided by the acting user) and
-- ACTIVE -> SUPERSEDED move; nothing changes what the account is or who asked for it.
CREATE FUNCTION station_bank_accounts_guard() RETURNS trigger
  LANGUAGE plpgsql SET search_path = public, pg_temp
  AS $$
  BEGIN
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

REVOKE ALL ON FUNCTION station_bank_accounts_check_insert() FROM PUBLIC;
REVOKE ALL ON FUNCTION station_bank_accounts_guard() FROM PUBLIC;

CREATE TRIGGER station_bank_accounts_check_insert BEFORE INSERT ON station_bank_accounts
  FOR EACH ROW EXECUTE FUNCTION station_bank_accounts_check_insert();
CREATE TRIGGER station_bank_accounts_guard BEFORE UPDATE ON station_bank_accounts
  FOR EACH ROW EXECUTE FUNCTION station_bank_accounts_guard();

-- ----------------------------------------------------------------------------------------------
-- 4b. Payouts (roadmap delta A1)
-- ----------------------------------------------------------------------------------------------

CREATE TABLE payouts (
  id                           uuid PRIMARY KEY,
  station_id                   uuid NOT NULL REFERENCES stations (id),
  operator_id                  uuid NOT NULL REFERENCES station_operators (id),
  -- The verified account the transfer goes to, as it was when the payout was recorded.
  bank_account_id              uuid NOT NULL REFERENCES station_bank_accounts (id),
  amount_piastres              bigint NOT NULL CHECK (amount_piastres > 0),
  -- As typed, for display; and normalised like a top-up reference (V6_1) for the uniqueness rule.
  bank_reference               text NOT NULL,
  bank_reference_key           text NOT NULL CHECK (bank_reference_key ~ '^[A-Z0-9]+$'),
  value_date                   date NOT NULL,
  note                         text,
  status                       text NOT NULL CHECK (status IN ('PENDING', 'POSTED', 'CANCELLED')),
  -- The threshold in force when the payout was recorded, kept so the row explains itself.
  approval_threshold_piastres  bigint NOT NULL CHECK (approval_threshold_piastres >= 0),
  approval_required            boolean NOT NULL,
  requested_by                 uuid NOT NULL,
  requested_at                 timestamptz NOT NULL,
  -- Who approved or cancelled it; NULL for a payout posted at once and while it waits.
  decided_by                   uuid,
  decided_at                   timestamptz,
  decision_reason              text,
  posted_at                    timestamptz,
  ledger_transaction_id        uuid REFERENCES ledger_transactions (id),
  created_at                   timestamptz NOT NULL,
  updated_at                   timestamptz NOT NULL,
  CONSTRAINT payouts_approval_follows_threshold CHECK (approval_required = (amount_piastres > approval_threshold_piastres)),
  -- A posted payout is exactly one that posted; nothing else may point at a posting.
  CONSTRAINT payouts_posted_has_posting CHECK ((status = 'POSTED') = (ledger_transaction_id IS NOT NULL)),
  CONSTRAINT payouts_posted_at CHECK ((status = 'POSTED') = (posted_at IS NOT NULL)),
  CONSTRAINT payouts_pending_awaits_approval CHECK (status <> 'PENDING' OR (approval_required AND decided_at IS NULL)),
  CONSTRAINT payouts_decider CHECK ((decided_at IS NULL) = (decided_by IS NULL)),
  -- Dual control: above the threshold a payout posts only on the word of a second person.
  CONSTRAINT payouts_two_person CHECK (
    status <> 'POSTED' OR NOT approval_required OR (decided_by IS NOT NULL AND decided_by <> requested_by)),
  CONSTRAINT payouts_at_once_has_no_approver CHECK (status <> 'POSTED' OR approval_required OR decided_by IS NULL),
  CONSTRAINT payouts_cancelled_by CHECK (status <> 'CANCELLED' OR (decided_by IS NOT NULL AND decision_reason IS NOT NULL))
);

-- One bank transfer pays once, platform-wide: a reference belongs to at most one live payout.
-- A cancelled payout frees its reference.
CREATE UNIQUE INDEX payouts_reference_live_key ON payouts (bank_reference_key) WHERE status IN ('PENDING', 'POSTED');
CREATE UNIQUE INDEX payouts_ledger_transaction_key ON payouts (ledger_transaction_id) WHERE ledger_transaction_id IS NOT NULL;
CREATE INDEX payouts_station_id_idx ON payouts (station_id, id);
CREATE INDEX payouts_operator_id_idx ON payouts (operator_id, id);
CREATE INDEX payouts_status_idx ON payouts (status, id);
CREATE INDEX payouts_bank_account_id_idx ON payouts (bank_account_id);
-- What waits for approval at one station (it reserves that much of the payable).
CREATE INDEX payouts_pending_idx ON payouts (station_id) WHERE status = 'PENDING';

COMMENT ON TABLE payouts IS 'A bank transfer to a station (A1): station payable to platform:bank. Above the threshold it waits for a second person.';

CREATE FUNCTION payouts_check_insert() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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

-- Every role, the owner included: only a PENDING payout moves, to POSTED or CANCELLED, decided by
-- the acting user; nothing changes where, how much or who recorded it.
CREATE FUNCTION payouts_guard() RETURNS trigger
  LANGUAGE plpgsql SET search_path = public, pg_temp
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

REVOKE ALL ON FUNCTION payouts_check_insert() FROM PUBLIC;
REVOKE ALL ON FUNCTION payouts_guard() FROM PUBLIC;

CREATE TRIGGER payouts_check_insert BEFORE INSERT ON payouts
  FOR EACH ROW EXECUTE FUNCTION payouts_check_insert();
CREATE TRIGGER payouts_guard BEFORE UPDATE ON payouts
  FOR EACH ROW EXECUTE FUNCTION payouts_guard();

-- ----------------------------------------------------------------------------------------------
-- 4c. Releases of held payables (B5 contract §4: fuelings.payable_held)
-- ----------------------------------------------------------------------------------------------

-- A fueling never changes (V7_1), so "Operations cleared this hold" is its own row. Append only.
CREATE TABLE payable_hold_releases (
  id               uuid PRIMARY KEY,
  fueling_id       uuid NOT NULL REFERENCES fuelings (id),
  station_id       uuid NOT NULL REFERENCES stations (id),
  operator_id      uuid NOT NULL REFERENCES station_operators (id),
  amount_piastres  bigint NOT NULL CHECK (amount_piastres > 0),
  reason           text NOT NULL CHECK (length(btrim(reason)) > 0),
  released_by      uuid NOT NULL,
  released_at      timestamptz NOT NULL
);

CREATE UNIQUE INDEX payable_hold_releases_fueling_key ON payable_hold_releases (fueling_id);
CREATE INDEX payable_hold_releases_station_id_idx ON payable_hold_releases (station_id, id);
CREATE INDEX payable_hold_releases_operator_id_idx ON payable_hold_releases (operator_id, id);

COMMENT ON TABLE payable_hold_releases IS 'Operations releases a held payable; the fueling row itself is immutable. A released amount counts as available for payout.';

CREATE FUNCTION payable_hold_releases_check_insert() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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

REVOKE ALL ON FUNCTION payable_hold_releases_check_insert() FROM PUBLIC;

CREATE TRIGGER payable_hold_releases_check_insert BEFORE INSERT ON payable_hold_releases
  FOR EACH ROW EXECUTE FUNCTION payable_hold_releases_check_insert();
CREATE TRIGGER payable_hold_releases_immutable BEFORE UPDATE OR DELETE ON payable_hold_releases
  FOR EACH ROW EXECUTE FUNCTION ledger_refuse_change();
CREATE TRIGGER payable_hold_releases_no_truncate BEFORE TRUNCATE ON payable_hold_releases
  FOR EACH STATEMENT EXECUTE FUNCTION ledger_refuse_change();

-- ----------------------------------------------------------------------------------------------
-- 4d. Two-person fueling reversals (PRD 6.4, roadmap delta A4)
-- ----------------------------------------------------------------------------------------------

CREATE TABLE fueling_reversals (
  id                     uuid PRIMARY KEY,
  fueling_id             uuid NOT NULL REFERENCES fuelings (id),
  -- The fueling's own tenants and amount, copied so each audience reads its reversals without a join.
  company_id             uuid NOT NULL REFERENCES companies (id),
  operator_id            uuid NOT NULL REFERENCES station_operators (id),
  station_id             uuid NOT NULL REFERENCES stations (id),
  amount_piastres        bigint NOT NULL CHECK (amount_piastres > 0),
  reason                 text NOT NULL CHECK (length(btrim(reason)) > 0),
  status                 text NOT NULL CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED')),
  requested_by           uuid NOT NULL,
  requested_at           timestamptz NOT NULL,
  decided_by             uuid,
  decided_at             timestamptz,
  decision_reason        text,
  ledger_transaction_id  uuid REFERENCES ledger_transactions (id),
  created_at             timestamptz NOT NULL,
  updated_at             timestamptz NOT NULL,
  CONSTRAINT fueling_reversals_decided CHECK ((status = 'PENDING') = (decided_at IS NULL)),
  CONSTRAINT fueling_reversals_decider CHECK ((decided_at IS NULL) = (decided_by IS NULL)),
  -- Two-person rule (PRD 6.4): the approver is never the requester.
  CONSTRAINT fueling_reversals_two_person CHECK (status <> 'APPROVED' OR decided_by <> requested_by),
  -- An approved reversal is exactly one that posted.
  CONSTRAINT fueling_reversals_approved_has_posting CHECK ((status = 'APPROVED') = (ledger_transaction_id IS NOT NULL)),
  CONSTRAINT fueling_reversals_rejection_has_reason CHECK (status <> 'REJECTED' OR decision_reason IS NOT NULL)
);

-- A fueling is reversed at most once: one request alive (pending or approved) per fueling. A
-- rejected request frees the fueling for another.
CREATE UNIQUE INDEX fueling_reversals_one_live ON fueling_reversals (fueling_id) WHERE status IN ('PENDING', 'APPROVED');
CREATE UNIQUE INDEX fueling_reversals_ledger_transaction_key ON fueling_reversals (ledger_transaction_id)
  WHERE ledger_transaction_id IS NOT NULL;
CREATE INDEX fueling_reversals_fueling_id_idx ON fueling_reversals (fueling_id);
CREATE INDEX fueling_reversals_company_id_idx ON fueling_reversals (company_id, id);
CREATE INDEX fueling_reversals_operator_id_idx ON fueling_reversals (operator_id, id);
CREATE INDEX fueling_reversals_station_id_idx ON fueling_reversals (station_id, id);
CREATE INDEX fueling_reversals_status_idx ON fueling_reversals (status, id);

COMMENT ON TABLE fueling_reversals IS 'A credit note for a completed fueling (PRD 6.4): requested by one person, approved by another, posted as the mirror of the fueling. The fueling row never changes.';

CREATE FUNCTION fueling_reversals_check_insert() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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

-- Every role, the owner included: only a PENDING request moves, decided by the acting user; an
-- approval must point at the ledger transaction that reverses this fueling's own posting.
CREATE FUNCTION fueling_reversals_guard() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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

REVOKE ALL ON FUNCTION fueling_reversals_check_insert() FROM PUBLIC;
REVOKE ALL ON FUNCTION fueling_reversals_guard() FROM PUBLIC;

CREATE TRIGGER fueling_reversals_check_insert BEFORE INSERT ON fueling_reversals
  FOR EACH ROW EXECUTE FUNCTION fueling_reversals_check_insert();
CREATE TRIGGER fueling_reversals_guard BEFORE UPDATE ON fueling_reversals
  FOR EACH ROW EXECUTE FUNCTION fueling_reversals_guard();

-- ----------------------------------------------------------------------------------------------
-- 5. Grants and row-level security
-- ----------------------------------------------------------------------------------------------

GRANT SELECT, INSERT ON station_payables TO zawed_app;
GRANT UPDATE (balance_piastres, updated_at) ON station_payables TO zawed_app;        -- the cached balance only
GRANT SELECT, INSERT ON station_bank_accounts TO zawed_app;
GRANT UPDATE (status, decided_by, decided_at, decision_reason, payouts_held_until, superseded_at, updated_at)
  ON station_bank_accounts TO zawed_app;                                               -- decisions only
GRANT SELECT, INSERT ON payouts TO zawed_app;
GRANT UPDATE (status, decided_by, decided_at, decision_reason, posted_at, ledger_transaction_id, updated_at)
  ON payouts TO zawed_app;                                                             -- decisions only
GRANT SELECT, INSERT ON payable_hold_releases TO zawed_app;                            -- append only
GRANT SELECT, INSERT ON fueling_reversals TO zawed_app;
GRANT UPDATE (status, decided_by, decided_at, decision_reason, ledger_transaction_id, updated_at)
  ON fueling_reversals TO zawed_app;                                                   -- decisions only

ALTER TABLE station_payables ENABLE ROW LEVEL SECURITY;
CREATE POLICY station_payables_by_scope ON station_payables
  USING (app_is_platform() OR (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id()))
  WITH CHECK (app_is_platform());

-- The station portal shows the verified account and a change that is waiting, masked; the history
-- of rejected and replaced accounts is Operations' own.
ALTER TABLE station_bank_accounts ENABLE ROW LEVEL SECURITY;
CREATE POLICY station_bank_accounts_by_scope ON station_bank_accounts
  USING (app_is_platform()
         OR (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id() AND status IN ('ACTIVE', 'PENDING')))
  WITH CHECK (app_is_platform());

-- A station sees the payouts that were made to it, not the ones still waiting or withdrawn.
ALTER TABLE payouts ENABLE ROW LEVEL SECURITY;
CREATE POLICY payouts_by_scope ON payouts
  USING (app_is_platform()
         OR (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id() AND status = 'POSTED'))
  WITH CHECK (app_is_platform());

ALTER TABLE payable_hold_releases ENABLE ROW LEVEL SECURITY;
CREATE POLICY payable_hold_releases_by_scope ON payable_hold_releases
  USING (app_is_platform() OR (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id()))
  WITH CHECK (app_is_platform());

-- A reversal has two audiences, like the fueling it reverses. Each sees the ones that happened
-- (approved); requests and refusals are Operations' own.
ALTER TABLE fueling_reversals ENABLE ROW LEVEL SECURITY;
CREATE POLICY fueling_reversals_by_scope ON fueling_reversals
  USING (app_is_platform()
         OR (status = 'APPROVED'
             AND ((app_scope_type() = 'COMPANY' AND company_id = app_scope_id())
                  OR (app_scope_type() = 'OPERATOR' AND operator_id = app_scope_id()))))
  WITH CHECK (app_is_platform());

-- The station statement: an operator reads the payable accounts of its own stations, their entries
-- and the transactions those entries belong to. Nothing else of the ledger: not the bank side of a
-- payout, not a company's wallet or receivable entries. station_payables (one row per station,
-- carrying the operator) is the index the policies ask.
CREATE POLICY ledger_accounts_operator_read ON ledger_accounts FOR SELECT
  USING (app_scope_type() = 'OPERATOR'
         AND id IN (SELECT p.account_id FROM station_payables p WHERE p.operator_id = app_scope_id()));

CREATE POLICY ledger_entries_operator_read ON ledger_entries FOR SELECT
  USING (app_scope_type() = 'OPERATOR'
         AND account_id IN (SELECT p.account_id FROM station_payables p WHERE p.operator_id = app_scope_id()));

CREATE POLICY ledger_transactions_operator_read ON ledger_transactions FOR SELECT
  USING (app_scope_type() = 'OPERATOR'
         AND EXISTS (SELECT 1 FROM ledger_entries e
                      WHERE e.transaction_id = ledger_transactions.id
                        AND e.account_id IN (SELECT p.account_id FROM station_payables p
                                              WHERE p.operator_id = app_scope_id())));

-- ----------------------------------------------------------------------------------------------
-- 6. Two-step verification for station managers (ADR 0009, roadmap delta A3)
-- ----------------------------------------------------------------------------------------------

-- From this release a station manager signs in with a second factor (Role.requiresMfa) and enrols at
-- the next sign-in. A session opened before, with a password alone, would keep reading bank details
-- and payouts until it expired: such sessions end now.
UPDATE sessions
   SET revoked_at = now(), revoked_reason = 'MFA_REQUIRED'
 WHERE revoked_at IS NULL
   AND user_id IN (SELECT id FROM users WHERE role = 'STATION_MANAGER' AND NOT mfa_enabled);
