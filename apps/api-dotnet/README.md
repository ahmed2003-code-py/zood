# ZAWED API (ASP.NET Core)

The .NET port of `apps/api` ([ADR 0029](../../docs/adr/0029-backend-on-aspnet-core.md); plan in
[`docs/architecture/dotnet-migration-plan.md`](../../docs/architecture/dotnet-migration-plan.md)). It serves the same HTTP
contract on the same PostgreSQL schema. Until the cutover, `apps/api` (Java) is the API that zawed-dev runs.

## Requirements

- .NET 10 SDK. On the development machine it is in `~/.dotnet`:
  `export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`.
- Docker, for the tests (Testcontainers starts `postgres:18`) and for local runs without a PostgreSQL install.

## Build and test

```bash
cd apps/api-dotnet
dotnet build Zawed.slnx
dotnet test Zawed.slnx                                 # every test, against a real PostgreSQL 18 in Docker
dotnet test Zawed.slnx --filter "FullyQualifiedName~MigrationRunnerTests"
```

The first run pulls `postgres:18`. One container serves the whole run, and Testcontainers' reaper removes it at
the end. Warnings are errors (`Directory.Build.props`).

## Run locally

```bash
docker run -d --rm --name zawed-pg -e POSTGRES_USER=zawed -e POSTGRES_PASSWORD=zawed -e POSTGRES_DB=zawed -p 5432:5432 postgres:18
PGPASSWORD=zawed dotnet run --project src/Zawed.Api      # http://localhost:8080/api/v1/meta, /api/v1/docs/
```

Without `SPRING_PROFILES_ACTIVE=deployed` the API uses the committed development values: the same ones as the Java
`application.yaml`, so a local database made by either API works with the other. On start it applies the pending
migrations as the database owner, then serves as `zawed_app`.

## Configuration

The same environment variables as the Java API (`Platform/Config/ZawedConfiguration.cs` maps them):

| Variable | Meaning |
|---|---|
| `SPRING_PROFILES_ACTIVE=deployed` (or `ZAWED_PROFILE`) | Internet-facing: no development defaults, Secure cookies, forwarded headers, JSON logs |
| `ZAWED_ENVIRONMENT` | `dev`, `staging` or `production` when deployed |
| `ZAWED_CORS_ALLOWED_ORIGINS` | Comma-separated https origins |
| `ZAWED_JWT_SECRET`, `ZAWED_DATA_KEY`, `ZAWED_DATA_KEY_ID`, `ZAWED_DATA_KEYS_PREVIOUS`, `ZAWED_LOOKUP_KEY`, `ZAWED_BOOTSTRAP_TOKEN` | Secrets and the key ring (ADR 0027) |
| `ZAWED_MFA_REQUIRED` | `false` only on dev (production refuses to start) |
| `ZAWED_API_DOCS` | `false` hides `/api/v1/docs/` |
| `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, `PGPASSWORD` | The database (the owner; the app switches to `zawed_app`) |
| `PORT` | HTTP port, 8080 by default |

A deployed instance with weak, missing or committed development secrets refuses to start (`DeploymentGuard`).

## Image

`Dockerfile` builds the image Railway runs (root directory `apps/api-dotnet`). It runs as a non-root user and
listens on `PORT`.

```bash
docker build -t zawed-api-dotnet .
```

## Layout

See `CLAUDE.md` in this folder: where code goes, and the conventions every module follows.
