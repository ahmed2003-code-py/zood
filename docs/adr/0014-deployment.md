# ADR 0014: Deployment

**Status:** Accepted, 2026-11-12. Amended by [ADR 0016](0016-separate-dev-project.md): `zawed-dev` is a separate project, not an environment inside `zood`.
**Source:** `docs/architecture/implementation-plan.md` §4, decision 14

## Context
The investor demo runs on Railway as the service `zood-web`, on `master`, and is frozen. New work needs a deployed API, database and web app that cannot affect it. Hosting for production financial data is decided later, in B7 (data residency and point-in-time recovery).

## Decision
- Deploy on Railway in a new environment, `zawed-dev`, with the API, PostgreSQL, Redis and web.
- Flyway migrations run before each deploy; readiness (not liveness) gates the deploy.
- The Railway environment `zawed-dev` is not created yet. Creating it needs the owner's explicit go-ahead.
- The frozen investor demo service `zood-web` is never touched.

## Consequences
- The demo cannot be broken by API work.
- Migrations are applied on `zawed-dev` before the new code serves traffic.
- B0 step 8 (create `zawed-dev`) waits for the owner's approval.
- This is a development environment, not a production choice; B7 revisits hosting.
