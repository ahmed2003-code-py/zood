# ADR 0006: Money and Volume

**Status:** Accepted, 2026-11-12
**Source:** `docs/architecture/implementation-plan.md` §4, decision 6

## Context
The web prototype already fixes the arithmetic in `src/domain/money.ts`: integer piastres, integer millilitres, half-up rounding. Soker's audit found decimal money converted to floating point in reports. The backend must produce the same numbers as the web and the POS.

## Decision
- Money is a `long` count of piastres everywhere; volume is a `long` count of millilitres everywhere.
- Database columns are `BIGINT` with `CHECK` constraints.
- No floating point anywhere, including exports.
- `money.test.ts` is ported to Java as golden tests.

## Consequences
- The arithmetic is identical on the web, the API and later the Kotlin POS.
- Reports and exports cannot drift by a piastre through float conversion.
- Rounding rules (half-up) must be implemented once in the `platform` module's `Money` and `Volume` types and reused.
- Ledger, wallet and statement totals can be compared exactly (B6 exit: agree to the piastre).
