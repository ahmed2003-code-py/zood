-- Ledger hardening after the B3 security review (findings 1, 4 and 6). V6 is deployed, so the
-- fixes come as their own migration; it sorts after V6 and before V7.

-- ----------------------------------------------------------------------------------------------
-- 1. A bank reference has one comparison key however it was typed or pasted.
--    The application now builds the key as: NFKC, digits of any script folded to 0-9, upper case,
--    and only A-Z and 0-9 kept (TopUps.referenceKey). V6 only folded ASCII case and spaces, so a
--    pasted right-to-left mark, a non-breaking space, a dash or Arabic digits gave the same
--    transfer a second key, and a second credit. Existing keys are rebuilt the same way, and the
--    database now refuses any key that is not in that shape.
-- ----------------------------------------------------------------------------------------------

UPDATE topup_requests
   SET bank_reference_key = regexp_replace(
         upper(translate(normalize(bank_reference, NFKC), '٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹', '01234567890123456789')),
         '[^A-Z0-9]', '', 'g');

ALTER TABLE topup_requests
  ADD CONSTRAINT topup_requests_reference_key_shape CHECK (bank_reference_key ~ '^[A-Z0-9]+$');

-- ----------------------------------------------------------------------------------------------
-- 4. A decided top-up never changes, and nothing changes who or how much a request is for.
--    Only a PENDING request moves (to CONFIRMED, REJECTED or CANCELLED, and on confirmation its
--    reference may be corrected to the bank statement's). This holds for every role, owner too.
-- ----------------------------------------------------------------------------------------------

CREATE FUNCTION topup_requests_guard() RETURNS trigger
  LANGUAGE plpgsql SET search_path = public, pg_temp
  AS $$
  BEGIN
    IF OLD.status <> 'PENDING' THEN
      RAISE EXCEPTION 'top-up % is % and can no longer change', OLD.id, OLD.status
        USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.company_id IS DISTINCT FROM OLD.company_id
       OR NEW.amount_piastres IS DISTINCT FROM OLD.amount_piastres
       OR NEW.transfer_date IS DISTINCT FROM OLD.transfer_date
       OR NEW.requested_by IS DISTINCT FROM OLD.requested_by
       OR NEW.requested_at IS DISTINCT FROM OLD.requested_at THEN
      RAISE EXCEPTION 'top-up % cannot change its company, amount, transfer date or requester', OLD.id
        USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END
  $$;

REVOKE ALL ON FUNCTION topup_requests_guard() FROM PUBLIC;

CREATE TRIGGER topup_requests_guard BEFORE UPDATE ON topup_requests
  FOR EACH ROW EXECUTE FUNCTION topup_requests_guard();

-- A wallet's cached balance is the only thing the application updates on it.
REVOKE UPDATE ON company_wallets FROM zawed_app;
GRANT UPDATE (balance_piastres, updated_at) ON company_wallets TO zawed_app;

-- ----------------------------------------------------------------------------------------------
-- 6. The nightly run happens once per Cairo night, whichever instance gets there first. The
--    advisory lock only stopped two runs overlapping; two instances could still run it one after
--    the other (ADR 0011 asks for a lease). A run by hand (MANUAL) is always allowed.
-- ----------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION ledger_reconcile(p_run_id uuid, p_trigger text, p_actor uuid, p_actor_role text,
                                            p_now timestamptz, p_request_id text)
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
