-- Date-range reads on fuelings (production readiness audit F6). Evidence: QueryPlanReport on
-- 5,000,000 synthetic fuelings over 12 months (docs/artifacts/production-hardening/performance-report.md).
--
-- 1. (company_id, evaluated_at): the company calendar, the company export and Ops filters by company
--    read one company's month instead of all of its history (largest company: activity for a month
--    4.2 s -> 0.16 s, export count 0.63 s -> 0.12 s).
-- 2. (operator_id, evaluated_at): the same for an operator and its stations (activity 0.75 s -> 0.09 s,
--    export count 0.75 s -> 0.07 s, a station's list for an old month 63 ms -> 12 ms).
-- 3. BRIN (evaluated_at): platform-wide ranges, the Ops calendar and the POS snapshot's limit usage
--    since the period starts (1.04 s -> 0.54 s and 1.16 s -> 0.63 s). Rows arrive in time order (an
--    offline capture at most days late), so a block range index fits; it is 80 kB at 5 M rows.
--
-- Not added: (station_id, evaluated_at), which no measured query used once (2) existed, and a B-tree
-- on evaluated_at alone (107 MB) where the BRIN gave the same plans.
--
-- Write cost: two B-trees of about 194 MB each at 5 M rows; measured single-row insert cost rose from
-- 0.50 to 0.54 ms per row (about 8 %), a bulk insert of 20,000 rows from 3.1 s to 3.7 s.
--
-- Why not CREATE INDEX CONCURRENTLY: it was the plan, and it hangs under this Flyway setup. Flyway
-- (12.4, PostgreSQL transactional advisory lock, the default) keeps a second connection idle in a
-- transaction that holds a snapshot for the whole migration, and CREATE INDEX CONCURRENTLY waits for
-- every older snapshot to end: it waited on Flyway's own connection for good (observed: phase
-- "waiting for old snapshots", wait event virtualxid, blocker = Flyway's idle-in-transaction
-- session). That wait is on Flyway's session, not on the table's size, so the same is expected on an
-- empty table and in the test suite (not run: it would not finish). Switching to it
-- needs spring.flyway.postgresql.transactional-lock=false in application.yaml (Flyway then uses a
-- session lock), a V16__fuelings_time_indexes.sql.conf with executeInTransaction=false, and the
-- CONCURRENTLY keyword; QueryPlanReport (phase v16_concurrent) shows that combination working.
--
-- So these are plain builds in Flyway's transaction: all or nothing, and inserts into fuelings wait
-- while they run (measured on 5 M rows: 8.5 s, 10.7 s and 5.9 s as separate builds; 30.5 s for the
-- whole script through Flyway on a busier machine): about 2 s per million rows for each B-tree on
-- the test laptop. Once a pause of that size during a deploy matters, use the concurrent route.

CREATE INDEX IF NOT EXISTS fuelings_company_time_idx ON fuelings (company_id, evaluated_at);
CREATE INDEX IF NOT EXISTS fuelings_operator_time_idx ON fuelings (operator_id, evaluated_at);
CREATE INDEX IF NOT EXISTS fuelings_evaluated_at_brin ON fuelings USING brin (evaluated_at);
