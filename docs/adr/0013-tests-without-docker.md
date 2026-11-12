# ADR 0013: Tests Without Docker

**Status:** Accepted, 2026-11-12
**Source:** `docs/architecture/implementation-plan.md` §4, decision 13

## Context
Row-level security, row locks and database constraints are central to ZAWED, and they cannot be tested on H2. The development machine has no Docker and no system PostgreSQL, and the plan must work without them.

## Decision
- Tests run against real PostgreSQL through Zonky `io.zonky.test:embedded-postgres` 2.2.2 with the PostgreSQL 18.6 binaries (`embedded-postgres-binaries-bom` 18.6.0), locally and in CI.
- The same embedded PostgreSQL backs local development runs (`./mvnw spring-boot:test-run`, class `LocalApiApplication` under `src/test`), with its data in `apps/api/.localdb/` (git-ignored).
- No system-wide PostgreSQL install is needed on the development machine.

## Consequences
- RLS, locks, unique keys and balance constraints are tested on the real engine.
- No Docker dependency for developers or CI.
- Local development data survives restarts but is disposable: delete `.localdb/` for a clean database.
- Tests and local runs use PostgreSQL 18, so the deployed database should be PostgreSQL 18 too.
- The plan's step "install PostgreSQL locally" (§8 step 3) is no longer needed.
