# ADR 0011: Background Work

**Status:** Accepted, 2026-11-12
**Source:** `docs/architecture/implementation-plan.md` §4, decision 11

## Context
Money postings are synchronous, inside the caller's transaction. Everything else (notifications, live feed, soft alerts) happens after commit. Nagahny lost fire-and-forget background work on deploy. Periodic jobs include the nightly wallet reconciliation and retention jobs.

## Decision
- Periodic jobs use `@Scheduled` with a database lock, so only one instance runs each job.
- After-commit work goes through the Spring Modulith event outbox.
- Shutdown is graceful.

## Consequences
- No extra infrastructure such as a job queue or broker.
- Outbox events are written in the same transaction as the change, so they survive restarts and deploys.
- Jobs are leased, so a second instance does not run the same job twice.
- Handlers must be idempotent, because an outbox event can be delivered more than once.
