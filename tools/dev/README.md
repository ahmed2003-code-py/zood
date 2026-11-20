# Development helpers (B7)

Scripts used during B7 on the first laptop. None of them contains a secret: credentials always come from environment variables.

| File | What it does |
|---|---|
| `test-accounts.mjs` | Creates one test account per role on `zawed-dev` and funds the demo company's wallet through the two-person top-up flow. It needs MFA switched off (`ZAWED_MFA_REQUIRED=false`) and the seed data (`npm run seed:api` in `apps/web`). Env: `ZAWED_API_URL`, `ADMIN_EMAIL`, `ADMIN_PASSWORD`, `TEST_PASSWORD`, `OUT`. It is idempotent for existing users. |
| `chunked-suite.ps1` | Runs the API suite one package at a time, and retries a package only on the known environmental failures (embedded PostgreSQL port or connection contention when several Maven runs share the machine). Fix the paths at the top. |
| `precheck.sql` | Read-only pre-deploy checks: Flyway history, plate collisions, tanks over 1,500 L, top-ups, users, sizes. Run it with `railway ssh --service Postgres -- psql -U postgres -d railway -f - < precheck.sql` from `apps/api`. |
| `precheck.cjs` | The same checks through `pg`, for a database you can reach directly (`DB_URL`). |
| `cntrl.sql`, `cntrl2.sql` | Show which characters PostgreSQL's `[:cntrl:]` refuses: Unicode Cc, the same set as Java `Character.isISOControl`. They were used to align `Vehicles.validFleetCode` with V14. |
| `Hash.java` | Produces an Argon2id hash with `PasswordHasher`'s exact parameters, from `NEW_PASSWORD`. It is for an out-of-band password reset in a dev database only, recorded in `audit_log`. Run it with the `spring-security-crypto`, `bcprov` and `commons-logging` jars on the classpath. |
| `screenshot.cjs` | A full-page Playwright screenshot of a local HTML file. |
