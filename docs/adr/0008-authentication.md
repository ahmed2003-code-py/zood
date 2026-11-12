# ADR 0008: Authentication

**Status:** Accepted, 2026-11-12
**Source:** `docs/architecture/implementation-plan.md` §4, decision 8

## Context
Nagahny proved a token design: refresh rotation with reuse detection, a session row checked on every request, and an httpOnly cookie. Its review also found refresh tokens in `localStorage` and no login rate limit. Logout and bans must take effect immediately.

## Decision
- Spring Security, deny by default (`anyRequest().authenticated()`) with an explicit public list.
- Passwords hashed with Argon2id.
- Access token: JWT, 15 minutes, held in memory by the web app.
- Refresh token: opaque, stored hashed, rotated on use, delivered in an httpOnly cookie; never in `localStorage`.
- Sessions live in PostgreSQL and are checked on every request.

## Consequences
- Revocation, logout and bans take effect immediately, not at token expiry.
- Every request costs a session lookup; this is accepted for immediate revocation.
- Reuse detection, a 60-second grace window and a role and status re-read on every refresh are carried over from Nagahny.
- Login is constant-time, with lockout counters and rate limits from B1.
