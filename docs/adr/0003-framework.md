# ADR 0003: Framework

**Status:** Accepted, 2026-11-12
**Source:** `docs/architecture/implementation-plan.md` §4, decision 3

## Context
The approved direction is a Spring Boot modular monolith. One database transaction must cover the whole money path, so module boundaries have to be enforced inside one deployable. The plan said to pin the latest stable Spring Boot at scaffold time.

## Decision
- Spring Boot 4.1.2, the latest stable on 2026-11-12.
- Spring Modulith 2.1.1, the release the Spring Initializr pairs with Spring Boot 4.1.2.
- springdoc-openapi 3.2.0 publishes the API description at `/api/v1/openapi.json`.
- One package per module under `com.zawed.api`; a Modulith verification test fails the build when a module reaches into another's internals.

## Consequences
- Module boundaries (only `ledger` writes money, `rules` has no framework dependency) are checked in tests, not by convention.
- The versions are pinned; upgrades are deliberate changes, not drift.
- Spring Modulith also provides the event outbox used for after-commit work (ADR 0011).
- This is the team's first Spring project, so B0 and B1 are kept small on purpose.
