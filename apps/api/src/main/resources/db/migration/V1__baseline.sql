-- ZAWED schema baseline.
--
-- Rules every later migration follows (docs/adr 0005, 0006, 0007):
--   * Primary keys are uuid (version 7, created by the application or by an offline device).
--   * Money is bigint piastres; fuel volume is bigint millilitres. Never numeric, real or double.
--   * Timestamps are timestamptz and hold UTC instants.
--   * Every foreign key gets an index in the migration that creates it.
--   * Migrations only move forward: a mistake is corrected by a new migration, never by editing one.
--   * Rows are not physically removed; "deleted" is a status (PRD 8.2).

COMMENT ON SCHEMA public IS
  'ZAWED core schema. Money: bigint piastres. Volume: bigint millilitres. Time: timestamptz in UTC. Keys: uuid v7.';
