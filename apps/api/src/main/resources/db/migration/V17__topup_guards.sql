-- B7 W1: the low findings of the production-hardening review (final-handover limitation 1).
--
--  1. A company's top-up request carries the claim and nothing else. V12's insert branch did not pin
--     the verification columns, the origin, the receiving account or the requester for a company
--     session, so one could insert a PENDING row that already looked verified (not reachable through
--     the API: TopUps.request writes none of them; now the database refuses it too).
--  2. Who cancels. A withdrawal (CANCELLED) is the company's own act, in the acting user's name, and
--     only of a request nobody has verified. TopUps has no other path to CANCELLED: Operations refuses
--     a claim or a verification by rejecting it, with a reason, audited as TOPUP_REJECTED.
--  3. Idempotency keys are purged after their retention (ADR 0022, IdempotencyKeyRetention). The
--     runtime role gets exactly the DELETE the job needs, and row-level security lets only that job
--     (the platform scope with no user) delete, and never a key younger than seven days.
--
-- V12's guard is replaced in place (CREATE OR REPLACE: the same function, so the same trigger), and
-- keeps every V12 rule in the same order; the V17 rules are marked.

-- ----------------------------------------------------------------------------------------------
-- 1 and 2. The top-up guard
-- ----------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION topup_requests_guard() RETURNS trigger
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
REVOKE ALL ON FUNCTION topup_requests_guard() FROM PUBLIC;

-- ----------------------------------------------------------------------------------------------
-- 3. Idempotency key retention
-- ----------------------------------------------------------------------------------------------

GRANT DELETE ON idempotency_keys TO zawed_app;

-- V13's policy was FOR ALL. With DELETE granted it would let every user delete their own keys, which
-- re-opens the double create a key exists to prevent; so a user now reads and inserts only.
DROP POLICY idempotency_keys_own ON idempotency_keys;
CREATE POLICY idempotency_keys_own_read ON idempotency_keys FOR SELECT
  USING (actor_id = app_user_id());
CREATE POLICY idempotency_keys_own_insert ON idempotency_keys FOR INSERT
  WITH CHECK (actor_id = app_user_id());

-- The purge job (IdempotencyKeyRetention) runs in the platform scope with no user, a reviewed
-- escalation listed in ArchitectureTests; a signed-in request always has a user. It reaches only keys
-- older than seven days, whatever retention the application is configured with: the floor is the
-- database's, like purge_device_nonces' replay window. A DELETE with a WHERE clause must also pass a
-- SELECT policy, hence the read policy over the same rows.
CREATE POLICY idempotency_keys_purge_read ON idempotency_keys FOR SELECT
  USING (app_is_platform() AND app_user_id() IS NULL AND created_at < now() - interval '7 days');
CREATE POLICY idempotency_keys_purge ON idempotency_keys FOR DELETE
  USING (app_is_platform() AND app_user_id() IS NULL AND created_at < now() - interval '7 days');

COMMENT ON TABLE idempotency_keys IS
  'Idempotency-Key per user and operation: the request body hash and the resource it created. Never updated; '
  'purged after zawed.idempotency.key-retention (30 days by default, never under 7; V17, ADR 0022).';
