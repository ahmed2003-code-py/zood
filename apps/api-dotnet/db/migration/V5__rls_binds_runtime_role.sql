-- Row-level security binds the runtime role, not the owner. (B1 review finding 1.)
--
-- V3 and V4 set FORCE ROW LEVEL SECURITY, which applies the policies to the table owner too. The
-- SECURITY DEFINER functions (the sign-in path, check_same_tenant) run as that owner, and the
-- sign-in path has no scope yet. Railway and the embedded test database connect as a superuser,
-- which skips row-level security, so this never showed; with an ordinary owner role every login
-- would be refused and the bootstrap would fail.
--
-- Giving each function its own platform scope (ALTER FUNCTION ... SET app.scope_type) is refused
-- to a non-superuser in PostgreSQL 15 and later. The owner is, by design, the trusted identity: it
-- runs the migrations and owns the narrow definer functions. The application never queries as the
-- owner: every pooled connection switches to zawed_app (Hikari connection-init-sql), which owns
-- nothing and is always subject to the policies. So FORCE is dropped, and the owner behaves the same
-- whether or not it is a superuser. Tested by NonSuperuserOwnerTests and MasterDataIsolationTests.

ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
ALTER TABLE audit_log NO FORCE ROW LEVEL SECURITY;

ALTER TABLE companies NO FORCE ROW LEVEL SECURITY;
ALTER TABLE departments NO FORCE ROW LEVEL SECURITY;
ALTER TABLE vehicles NO FORCE ROW LEVEL SECURITY;
ALTER TABLE rfid_tags NO FORCE ROW LEVEL SECURITY;
ALTER TABLE limit_rules NO FORCE ROW LEVEL SECURITY;
ALTER TABLE station_operators NO FORCE ROW LEVEL SECURITY;
ALTER TABLE stations NO FORCE ROW LEVEL SECURITY;
ALTER TABLE pos_devices NO FORCE ROW LEVEL SECURITY;
ALTER TABLE agents NO FORCE ROW LEVEL SECURITY;
ALTER TABLE fuel_prices NO FORCE ROW LEVEL SECURITY;
