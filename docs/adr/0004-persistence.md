# ADR 0004: Persistence

**Status:** Accepted, 2026-11-12
**Source:** `docs/architecture/implementation-plan.md` §4, decision 4

## Context
The money path needs explicit row locks (wallet row, then tag row, always in that order) and explicit SQL. Master data (companies, vehicles, stations) is plain CRUD. Database guarantees such as unique keys, balance constraints and row-level security must live in the schema.

## Decision
- Flyway SQL migrations own the schema; they are forward-only. Entities are validated against the schema.
- Spring Data JPA for master data.
- Plain SQL through `JdbcClient` for the ledger, the transaction engine and reports.
- `open-in-view` is off.

## Consequences
- Locks, `UPDATE ... WHERE status = 'PENDING'` row-count checks and `SELECT ... FOR UPDATE` are written out and visible in the money path.
- Master-data modules stay short because JPA does the CRUD.
- Two data-access styles live in one codebase; each module must stay clearly on one side.
- With `open-in-view` off, lazy loading outside a transaction fails loudly instead of hiding queries.
