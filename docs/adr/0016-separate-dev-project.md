# ADR 0016: Development environment is a separate Railway project

**Status:** Accepted, 2026-11-12
**Amends:** ADR 0014 (where `zawed-dev` lives)

## Context
ADR 0014 placed `zawed-dev` as a new environment inside the Railway project `zood`, beside the frozen investor demo. When it was created, the folder `apps/web` turned out to be linked to `zood`. Any Railway command run from that folder defaults to the demo's project. Inside one project, a mistyped environment or service flag would reach the demo.

## Decision
- `zawed-dev` is its own Railway project (ID `5e712b7f-0503-4cb6-9024-349c62f6e57c`, environment `production`), with services `Postgres` (PostgreSQL 18), `zawed-api` and `zawed-web`.
- `apps/api` is linked to `zawed-dev`. `apps/web` stays linked to `zood`, as the owner left it.
- Every deploy to `zawed-dev` passes `--project`, `--environment` and `--service` explicitly, never relying on a folder link.
- The API instance runs with `ZAWED_ENVIRONMENT=dev`, the profile `deployed`, and CORS limited to the `zawed-web` domain.

## Consequences
- Nothing in `zawed-dev` can change the demo, and the reverse.
- Railway's environment name `production` inside `zawed-dev` is a Railway default. What the API calls its environment comes from `ZAWED_ENVIRONMENT`.
- Staging and production for the pilot will be separate projects too, decided in B7.
- Railway deprecates `railway.json` (config as code) after 2026-12-01 in favour of `.railway/railway.ts`. Both `apps/api/railway.json` and the demo's `apps/web/railway.json` need migrating before then.
