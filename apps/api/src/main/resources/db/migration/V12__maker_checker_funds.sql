-- Production hardening P0 (docs/architecture/production-readiness-audit.md F1, F2; ADR 0021):
-- money enters a company wallet only after two different people agree.
--
--  1. Top-ups: Operations VERIFIES a transfer against the bank statement and records the amount that
--     actually arrived (PENDING_APPROVAL); a different user holding TOPUP_APPROVE APPROVES it, which
--     posts exactly the verified amount. The company's own figure stays as what it claimed.
--  2. A bank reference is unique per receiving ZAWED account, not across every bank in the world.
--  3. Wallet adjustments (credits that are not bank transfers) get the same two-person rule and
--     their own counter-account, platform:adjustments, so they never look like bank money.
--  4. A new platform role, OPS_MANAGER: Operations plus the approval of money entering wallets.
--
-- Rules from V1, V3, V6 and V9 apply. Like V9, the rules are the database's, not only the API's:
-- the verifier, the approver and the requester are the acting user, and the approver is never the
-- verifier.

-- ----------------------------------------------------------------------------------------------
-- 1. The OPS_MANAGER role
-- ----------------------------------------------------------------------------------------------

ALTER TABLE users DROP CONSTRAINT users_role_check;
ALTER TABLE users ADD CONSTRAINT users_role_check
  CHECK (role IN ('SUPER_ADMIN', 'OPS_MANAGER', 'OPS', 'FLEET_MANAGER', 'ACCOUNTANT', 'STATION_MANAGER'));
ALTER TABLE users DROP CONSTRAINT users_role_matches_scope;
ALTER TABLE users ADD CONSTRAINT users_role_matches_scope CHECK (
     (role IN ('SUPER_ADMIN', 'OPS_MANAGER', 'OPS') AND scope_type = 'PLATFORM')
  OR (role IN ('FLEET_MANAGER', 'ACCOUNTANT') AND scope_type = 'COMPANY')
  OR (role = 'STATION_MANAGER' AND scope_type = 'OPERATOR'));

-- ----------------------------------------------------------------------------------------------
-- 2. Top-ups: verify, then approve
-- ----------------------------------------------------------------------------------------------

ALTER TABLE topup_requests
  ADD COLUMN origin                    text NOT NULL DEFAULT 'COMPANY_REQUEST'
                                         CHECK (origin IN ('COMPANY_REQUEST', 'OPS_RECORDED')),
  ADD COLUMN receiving_account         text NOT NULL DEFAULT 'MAIN' CHECK (receiving_account ~ '^[A-Z0-9_]{1,32}$'),
  ADD COLUMN verified_amount_piastres  bigint CHECK (verified_amount_piastres > 0),
  ADD COLUMN verified_by               uuid,
  ADD COLUMN verified_at               timestamptz,
  ADD COLUMN evidence_reference        text CHECK (length(evidence_reference) <= 200),
  -- Top-ups confirmed by one person before this migration. Never set on a new row (trigger below).
  ADD COLUMN legacy_single_step        boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN topup_requests.amount_piastres IS
  'What the company said it transferred (or, for OPS_RECORDED, what Operations recorded). Never what is credited.';
COMMENT ON COLUMN topup_requests.verified_amount_piastres IS
  'What arrived, as Operations read it on the bank statement. The only amount ever credited.';
COMMENT ON COLUMN topup_requests.receiving_account IS
  'Which ZAWED bank account received the transfer (zawed.finance.receiving-accounts). A reference is unique per account.';

-- The V6_1 guard allowed a change only while PENDING: it would refuse this backfill of decided rows
-- and the approval of a verified top-up. It is replaced below by a guard that keeps all its rules.
DROP TRIGGER topup_requests_guard ON topup_requests;
DROP FUNCTION topup_requests_guard();

-- Existing rows: a credit by Operations without a company request becomes OPS_RECORDED; every
-- confirmed row was verified and decided by the same person, in one step, for the amount it posted.
UPDATE topup_requests SET origin = 'OPS_RECORDED' WHERE requested_by IS NULL;
UPDATE topup_requests
   SET verified_amount_piastres = amount_piastres, verified_by = decided_by, verified_at = decided_at,
       legacy_single_step = true
 WHERE status = 'CONFIRMED';

ALTER TABLE topup_requests DROP CONSTRAINT topup_requests_status_check;
ALTER TABLE topup_requests ADD CONSTRAINT topup_requests_status_check
  CHECK (status IN ('PENDING', 'PENDING_APPROVAL', 'CONFIRMED', 'REJECTED', 'CANCELLED'));
ALTER TABLE topup_requests DROP CONSTRAINT topup_requests_decided;
ALTER TABLE topup_requests ADD CONSTRAINT topup_requests_decided
  CHECK ((status IN ('PENDING', 'PENDING_APPROVAL')) = (decided_at IS NULL));
ALTER TABLE topup_requests ADD CONSTRAINT topup_requests_verified CHECK (
  (verified_amount_piastres IS NULL) = (verified_by IS NULL) AND (verified_by IS NULL) = (verified_at IS NULL));
ALTER TABLE topup_requests ADD CONSTRAINT topup_requests_verified_before_approval
  CHECK (status NOT IN ('PENDING_APPROVAL', 'CONFIRMED') OR verified_by IS NOT NULL);
ALTER TABLE topup_requests ADD CONSTRAINT topup_requests_two_person
  CHECK (status <> 'CONFIRMED' OR legacy_single_step OR (decided_by IS NOT NULL AND decided_by <> verified_by));
ALTER TABLE topup_requests ADD CONSTRAINT topup_requests_ops_recorded_is_verified
  CHECK (origin <> 'OPS_RECORDED' OR legacy_single_step OR status = 'CANCELLED' OR verified_by IS NOT NULL);

-- One transfer credits once: a reference belongs to at most one live top-up per receiving account.
DROP INDEX topup_requests_reference_live_key;
CREATE UNIQUE INDEX topup_requests_reference_live_key ON topup_requests (receiving_account, bank_reference_key)
  WHERE status IN ('PENDING', 'PENDING_APPROVAL', 'CONFIRMED');
CREATE INDEX topup_requests_awaiting_approval_idx ON topup_requests (id) WHERE status = 'PENDING_APPROVAL';

-- The company may withdraw only a request nobody has verified yet.
DROP POLICY topup_requests_decide ON topup_requests;
CREATE POLICY topup_requests_decide ON topup_requests FOR UPDATE
  USING (app_is_platform() OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id() AND status = 'PENDING'))
  WITH CHECK (app_is_platform()
              OR (app_scope_type() = 'COMPANY' AND company_id = app_scope_id() AND status = 'CANCELLED'));

-- Every role, the owner included. Replaces the V6_1 guard (dropped above), keeping all of its rules.
CREATE FUNCTION topup_requests_guard() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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
    IF NEW.status IN ('CONFIRMED', 'REJECTED') AND NEW.decided_by IS DISTINCT FROM app_user_id() THEN
      RAISE EXCEPTION 'top-up %: the decider must be the acting user', OLD.id USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NEW.status = 'CONFIRMED' AND OLD.status <> 'PENDING_APPROVAL' THEN
      RAISE EXCEPTION 'top-up %: only a verified top-up is approved', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;
REVOKE ALL ON FUNCTION topup_requests_guard() FROM PUBLIC;
CREATE TRIGGER topup_requests_guard BEFORE INSERT OR UPDATE ON topup_requests
  FOR EACH ROW EXECUTE FUNCTION topup_requests_guard();
CREATE TRIGGER topup_requests_no_delete BEFORE DELETE OR TRUNCATE ON topup_requests
  FOR EACH STATEMENT EXECUTE FUNCTION ledger_refuse_change();

-- ----------------------------------------------------------------------------------------------
-- 3. Wallet adjustments
-- ----------------------------------------------------------------------------------------------

ALTER TABLE ledger_accounts DROP CONSTRAINT ledger_accounts_kind_check;
ALTER TABLE ledger_accounts ADD CONSTRAINT ledger_accounts_kind_check
  CHECK (kind IN ('PLATFORM_BANK', 'PLATFORM_ADJUSTMENT', 'COMPANY_WALLET', 'COMPANY_RECEIVABLE', 'STATION_PAYABLE'));
ALTER TABLE ledger_accounts DROP CONSTRAINT ledger_accounts_owner_matches_kind;
ALTER TABLE ledger_accounts ADD CONSTRAINT ledger_accounts_owner_matches_kind CHECK (
     (kind IN ('PLATFORM_BANK', 'PLATFORM_ADJUSTMENT') AND company_id IS NULL AND station_id IS NULL)
  OR (kind IN ('COMPANY_WALLET', 'COMPANY_RECEIVABLE') AND company_id IS NOT NULL AND station_id IS NULL)
  OR (kind = 'STATION_PAYABLE' AND company_id IS NULL AND station_id IS NOT NULL));
INSERT INTO ledger_accounts (id, code, kind, company_id, station_id, created_at)
  VALUES (uuidv7(), 'platform:adjustments', 'PLATFORM_ADJUSTMENT', NULL, NULL, now());
COMMENT ON COLUMN ledger_accounts.kind IS
  'PLATFORM_ADJUSTMENT: the counter-account of approved wallet credits that are not bank transfers (V12).';

CREATE TABLE wallet_adjustments (
  id                     uuid PRIMARY KEY,
  company_id             uuid NOT NULL REFERENCES companies (id),
  amount_piastres        bigint NOT NULL CHECK (amount_piastres > 0),
  reason                 text NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 500),
  status                 text NOT NULL CHECK (status IN ('PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'CANCELLED')),
  requested_by           uuid NOT NULL,
  requested_at           timestamptz NOT NULL,
  decided_by             uuid,
  decided_at             timestamptz,
  decision_reason        text,
  ledger_transaction_id  uuid REFERENCES ledger_transactions (id),
  created_at             timestamptz NOT NULL,
  updated_at             timestamptz NOT NULL,
  CONSTRAINT wallet_adjustments_decided CHECK ((status = 'PENDING_APPROVAL') = (decided_at IS NULL)),
  CONSTRAINT wallet_adjustments_decider CHECK ((decided_at IS NULL) = (decided_by IS NULL)),
  CONSTRAINT wallet_adjustments_two_person CHECK (status <> 'APPROVED' OR decided_by <> requested_by),
  CONSTRAINT wallet_adjustments_cancelled_by_requester CHECK (status <> 'CANCELLED' OR decided_by = requested_by),
  CONSTRAINT wallet_adjustments_approved_has_posting CHECK ((status = 'APPROVED') = (ledger_transaction_id IS NOT NULL)),
  CONSTRAINT wallet_adjustments_rejection_has_reason CHECK (status <> 'REJECTED' OR decision_reason IS NOT NULL)
);

CREATE INDEX wallet_adjustments_company_id_idx ON wallet_adjustments (company_id, id);
CREATE INDEX wallet_adjustments_status_idx ON wallet_adjustments (status, id);
CREATE UNIQUE INDEX wallet_adjustments_ledger_transaction_key ON wallet_adjustments (ledger_transaction_id)
  WHERE ledger_transaction_id IS NOT NULL;

COMMENT ON TABLE wallet_adjustments IS
  'A wallet credit that is not a bank transfer (goodwill, correction). One person asks with a reason, a different person approves; approval posts platform:adjustments to the wallet.';

CREATE FUNCTION wallet_adjustments_guard() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp
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
REVOKE ALL ON FUNCTION wallet_adjustments_guard() FROM PUBLIC;
CREATE TRIGGER wallet_adjustments_guard BEFORE INSERT OR UPDATE ON wallet_adjustments
  FOR EACH ROW EXECUTE FUNCTION wallet_adjustments_guard();
CREATE TRIGGER wallet_adjustments_no_delete BEFORE DELETE OR TRUNCATE ON wallet_adjustments
  FOR EACH STATEMENT EXECUTE FUNCTION ledger_refuse_change();

GRANT SELECT, INSERT ON wallet_adjustments TO zawed_app;
GRANT UPDATE (status, decided_by, decided_at, decision_reason, ledger_transaction_id, updated_at)
  ON wallet_adjustments TO zawed_app;

-- Platform only: an adjustment names the company, but the company reads its effect in the wallet
-- statement (source ADJUSTMENT), not the internal request.
ALTER TABLE wallet_adjustments ENABLE ROW LEVEL SECURITY;
CREATE POLICY wallet_adjustments_platform ON wallet_adjustments
  USING (app_is_platform()) WITH CHECK (app_is_platform());
