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

## Amendment (B7 path matching, 2026-11-20)
A limit keyed by path counts a request only if it sees the path as Spring routes it. An independent review found that the sign-in limit compared the raw request URI, so `POST /api/v1/auth/logi%6E` reached the sign-in step without being counted. Two rules now hold:
- A request whose path is not already canonical is refused with `400 PATH_NOT_CANONICAL` before any limit runs (`platform/web/CanonicalPathFilter.java`).
- A limit or allow-list keyed by path matches with Spring Security's `PathPatternRequestMatcher`, never with a string comparison of `getRequestURI()` (`platform/security/SecurityFilters.java`). `ArchitectureTests` lists every class that reads the raw path.

The limits themselves are unchanged. Limits keyed by device, agent, user or email were never affected, nor were account lockout and the MFA challenge's five-attempt cap.
