# Deploy and roll forward

**Environment today:** Railway project `zawed-dev` (id `5e712b7f-0503-4cb6-9024-349c62f6e57c`), environment `production`. It has three services: `zawed-api`, `zawed-web` and `Postgres`. Production hosting is decided at the pilot.

## Before

1. The commit is on `develop`, and `zawed-release-gate` passed on it. That means:
   - API: `mvnw verify`, run on its own;
   - web: `npm run check`, `build` and `e2e`.
2. Read the migrations the deploy brings (`apps/api/src/main/resources/db/migration`) and their pre-checks. Flyway applies them at start-up, and **they are forward-only**.
3. When there are migrations, take a backup: see [backup-and-restore.md](backup-and-restore.md).

## Deploy

Always deploy from a clean worktree of the exact commit, never from the working tree. `railway up` uploads the folder as it is.

```bash
git worktree add --detach <scratchpad>/release-wt <sha>
cd <scratchpad>/release-wt/apps/api
railway up --ci --project 5e712b7f-0503-4cb6-9024-349c62f6e57c --environment production --service zawed-api
# then immediately, so the web never calls endpoints that are not there yet:
cd ../web
railway up --ci --project 5e712b7f-0503-4cb6-9024-349c62f6e57c --environment production --service zawed-web
```

**Gotchas:**
- Run `railway up` from `apps/api` or `apps/web`, not from the worktree root. A root build fails with "No start command".
- The API goes first whenever the web uses new endpoints. When the API removes an endpoint, deploy the two back to back.

## Verify

- `GET https://zawed-api-production.up.railway.app/api/v1/meta` returns the expected `schemaVersion`.
- `GET /api/v1/health` returns `UP`.
- Flyway history shows `success = t` for every new version:

  ```sql
  SELECT version, success, installed_on FROM flyway_schema_history ORDER BY installed_rank DESC LIMIT 5;
  ```

  Run it with `railway ssh --service Postgres -- psql -U postgres -d railway -f - < check.sql`, from `apps/api`, which is linked.
- The web answers 200, and the release's screens load.
- Sign in once as Ops on a test company.

Remove the worktree afterwards: `git worktree remove <path>`.

## When a deploy fails

| Symptom | What happened | Do |
|---|---|---|
| The build fails | Nothing changed. The previous deployment keeps serving. | Fix, then redeploy. |
| The API does not start, and Railway keeps the old deployment | Usually a migration failed. It rolled back inside its own transaction, and the earlier migrations stayed applied. | Read the deploy logs. Fix the data or the migration in a **new** commit (never edit an applied migration). Redeploy. |
| The API started, but behaves wrongly | A code bug | **Roll forward**: fix, gate, deploy. |

**Rolling back is not a plan.** The code depends on the schema it shipped with, and migrations are forward-only. Redeploying old code after a migration is safe only when the release notes say the migration is additive and the old code ignores it. If you ever must undo data, use [backup-and-restore.md](backup-and-restore.md).

## After

- Update the deploy state in memory (`zawed-production-plan.md`) and in the phase summary.
- Watch Sentry and the logs for 30 minutes, once a DSN is set.
