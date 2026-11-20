# Backup and restore

**Targets** (implementation plan, B7): lose at most 1 hour of data (RPO), and be back within 4 hours (RTO).

**Status, 2026-11-19:**
- Automatic backups and point-in-time recovery are **deferred to the pilot's hosting choice** (owner decision).
- Until then a backup is a manual `pg_dump` taken before every deploy with migrations. Its RPO is "since the last dump".
- Railway offers PITR on the Postgres service (`railway postgres pitr status|enable --service Postgres`). Enable it on the pilot host and rehearse this runbook again with it.

## The database cannot be reached from outside

The `zawed-dev` Postgres has no public URL, on purpose. Every command below runs `psql` or `pg_dump` inside the Postgres container through `railway ssh`, from `apps/api`, which is linked to `zawed-dev`.

## Take a backup

```bash
cd apps/api
railway ssh --service Postgres -- pg_dump -U postgres -d railway --no-owner \
  | grep -v '^Using SSH key' > ~/zawed-backups/zawed-dev-$(date +%Y%m%d-%H%M).sql
```

**Check it:**
- it ends with an `\unrestrict` line, which means the dump is complete;
- `grep -c '^COPY '` matches the number of tables (34 at schema 11).

The file holds the platform's data, including encrypted bank details and MFA secrets. Keep it on the owner's machine only. Never commit it or upload it anywhere.

## Restore

### 1. Into a separate database (rehearsal, or to recover rows)

This never touches the live database.

```bash
railway ssh --service Postgres -- psql -U postgres -d railway -c "DROP DATABASE IF EXISTS restore_rehearsal" -c "CREATE DATABASE restore_rehearsal"
railway ssh --service Postgres -- psql -U postgres -d restore_rehearsal -v ON_ERROR_STOP=1 -q -f - < ~/zawed-backups/<file>.sql
```

Then compare row counts, RLS policies and triggers with the source (`pg_policies`, `pg_trigger`). Copy back only what is needed, with a reviewed SQL script. Drop the database afterwards.

### 2. Replacing the live database (disaster)

1. Stop the API. In Railway, scale `zawed-api` to zero or remove its deployment, so nothing writes during the restore.
2. On a **new** server, create the roles first. They are cluster-level and **not in `pg_dump`**: today `zawed_app` (login, no superuser, no BYPASSRLS), with the password the API's variables expect. Then create the database.
3. Restore the dump, as in step 1, into that database.
4. Point the API's `PG*` variables at it, and start the API. Flyway sees the schema version and applies only newer migrations.
5. Verify, using [deploy.md](deploy.md) → Verify, then do one read and one write as Ops on a test company.
6. Write down what was lost, everything after the dump's time, and tell the affected companies and stations.

## Rehearsal log

| Date | What | Result |
|---|---|---|
| 2026-11-19 | Restored `zawed-dev-before-V12-20261119-1415.sql` (170 KB, schema 11) into `restore_rehearsal` on the `zawed-dev` server | Restored in 9 s with 34 tables, 42 RLS policies, 26 triggers and 1 user. The database was then dropped. The roles needed no step because the server already had them. **Not yet rehearsed:** a restore onto a new server, which is step 2. Do it on the pilot host. |
