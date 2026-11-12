# ADR 0015: Base Package

**Status:** Accepted, 2026-11-12
**Source:** `docs/architecture/implementation-plan.md` §4, decision 15

## Context
Java package names appear in every source file, in Spring Modulith module detection and in the Maven coordinates. Renaming them later touches the whole codebase. The product is branded ZAWED (زوّد).

## Decision
- The base package is `com.zawed`; the API's application class lives in `com.zawed.api` (Maven `com.zawed:zawed-api`).
- Each backend module is one package directly under `com.zawed.api`, for example `com.zawed.api.ledger`. `com.zawed.api.platform` is the shared kernel, declared as an open module.

## Consequences
- The name is fixed before any code exists, when it costs nothing.
- Modulith treats each direct sub-package of `com.zawed.api` as a module; `ModularityTests` enforces the boundaries.
- `com.zawed` leaves room for other JVM code later (for example the Kotlin POS sharing the rules module).
- The package matches the brand and not the old working name ZOOD.
- Any later rename would be a repository-wide change and would need its own ADR.
