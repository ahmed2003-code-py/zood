# ADR 0001: Backend Language

**Status:** Accepted, 2026-11-12
**Source:** `docs/architecture/implementation-plan.md` §4, decision 1

## Context
The approved direction is a Spring Boot modular monolith. The local machine already has JDK 25 LTS and JDK 21 installed. The domain (money, volume, fueling outcomes, ledger postings) is a set of small, strict value types and a closed set of outcomes.

## Decision
- The backend is written in Java 25 (LTS).
- Domain types use records and sealed types.

## Consequences
- No new toolchain to install; the language is already on the development machine.
- The Spring ecosystem is largest for Java, so examples, libraries and hiring are easiest.
- Records and sealed types let `Money`, `Volume` and the fueling outcomes be modelled as exact, closed types.
- `JAVA_HOME` must point at JDK 25, not JDK 21, on every machine and in CI.
