# ADR 0002: Build Tool

**Status:** Accepted, 2026-11-12
**Source:** `docs/architecture/implementation-plan.md` §4, decision 2

## Context
The backend is a single service in `apps/api`. The local machine has Maven 3.9.11 and no Gradle. The check for the API is `./mvnw verify`.

## Decision
- The build tool is Maven.
- The Maven wrapper (`mvnw`) is committed, so every machine and CI run the same version.

## Consequences
- Nothing extra to install; Maven is already present.
- One `pom.xml` is the simplest layout for a single service.
- `./mvnw verify` is the one command for the API check, locally and in CI.
- If the backend ever splits into several services, this choice can be revisited with a new ADR.
