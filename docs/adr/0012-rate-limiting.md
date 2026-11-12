# ADR 0012: Rate Limiting

**Status:** Accepted, 2026-11-12
**Source:** `docs/architecture/implementation-plan.md` §4, decision 12

## Context
Egyptian mobile carriers put many users behind one IP address, so a per-IP limit would block legitimate users. Soker had no login rate limit. The security baseline requires limits on login, PIN entry, refresh, activation, fueling and exports.

## Decision
- Limits are per authenticated user or device.
- Per-IP limits apply only to anonymous requests.
- The limiter is Redis-backed when deployed.

## Consequences
- Many users on one carrier IP do not lock each other out.
- Anonymous endpoints (login, forgot password, activation) are still protected by IP and by lockout counters.
- Redis becomes part of the deployed environment (ADR 0014).
- Locally and in tests, an in-memory limiter is needed because Redis is not required there.
