# Test fixtures

| File | What it is | How it was made |
|---|---|---|
| `flyway-12.4-history.tsv` | The `flyway_schema_history` rows real Flyway 12.4.0 wrote for the 25 migrations | `RunFlyway.java` against an empty `postgres:18`, 2026-11-21 |
| `flyway-12.4-at-v16.sql` | A whole database migrated by real Flyway to V16 (`pg_dump`), history included: the state of `zawed-dev` before B7 | `RunFlyway.java ... 16`, then `pg_dump` |
| `flyway-12.4-at-v23.schema.sql` | `pg_dump --schema-only` of a database migrated by real Flyway to V23 | `RunFlyway.java`, then `pg_dump --schema-only` |
| `java-golden-vectors.properties` | An Argon2id hash, AES-GCM values (key 1 and key 200) and lookup hashes written by the Java algorithms | `Golden.java` with spring-security-crypto 7.1.2 |
| `java-openapi.json` | The Java API's committed OpenAPI document (`apps/api/openapi/openapi.json`) | copied; `OpenApiParityTests` fails if it drifts from the Java file while that exists |

To regenerate the Flyway fixtures (needs the JDK and `~/.m2` with flyway-core 12.4.0, flyway-database-postgresql,
postgresql, jackson 3, commons-text and commons-lang3):

```bash
docker run -d --rm --name flyway-fixture -e POSTGRES_PASSWORD=postgres -p 55432:5432 postgres:18
java -cp "<the jars>" RunFlyway.java jdbc:postgresql://localhost:55432/postgres postgres postgres ../../../db/migration [target]
docker exec flyway-fixture pg_dump -U postgres -d postgres > flyway-12.4-at-v16.sql
docker stop flyway-fixture
```
