# ADR 0008: Authentication

**Status:** Accepted, 2026-11-12. Amended 2026-11-20 (one sign-in page for every role, below).
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

## Amendment, 2026-11-20: one sign-in page for every role

**Decision by the owner (2026-11-20), delivered in B7 W10.** The Super Admin signs in at `/login`, like every other role. The role the API returns decides which portal opens. This replaces B1's separate admin door, which had its own page (`/admin/login`), its own endpoints (`/api/v1/admin/auth/login`, `/refresh` and `/logout`), its own cookie (`zawed_admin_rt`, `Path=/api/v1/admin/auth`) and a "wrong door" refusal.

### What changed
- **API:**
  - `AdminAuthController` and the `Door` type are gone.
  - The admin paths are no longer public. Without a token they answer `401 UNAUTHENTICATED`, like any other unknown route.
  - The `WRONG_DOOR` failure reason is no longer written.
- **Cookie:** there is one refresh cookie, `zawed_rt` (`Path=/api/v1/auth`), for every role.
- **Session rows:**
  - `sessions.door` and `mfa_challenges.door` stay in the schema as history, so no migration was needed. New rows always record `USER`.
  - A session started at the old door (`door = 'ADMIN'`) is never refreshed at `/auth/refresh`, so nothing the removed door issued outlives it. Its holder signs in again at `/login`. The browser cannot end it at `/auth/logout` (its old cookie `zawed_admin_rt` was scoped to the removed path and is never sent there); it ends from the session list, or by itself at the 12-hour platform idle limit, and its last access token expires within 15 minutes of the deploy. On `zawed-dev` only test sessions existed when the door was removed.
- **Audit:** `LOGIN_SUCCEEDED` no longer carries a `door` field. Every other audit entry is unchanged: `LOGIN_FAILED` with its reason, `ACCOUNT_LOCKED`, `MFA_FAILED`, `MFA_ENROLLED`, `LOGOUT` and `SESSION_REVOKED`.
- **Web:**
  - `LoginPage` serves every role and has no admin variant.
  - `/admin/login` forwards to `/login` and keeps `?next=`, so old bookmarks work.
  - `safeNext` still refuses both paths as a target.
  - The page drops the key `zawed.auth.door`, which an older version left in `localStorage`.

### Why security is preserved
The separate door was a usability boundary, not a security one. The protections that matter run inside the same sign-in path for every role, and none of them depended on the door:
- **The same answer and comparable timing.** An unknown email and a wrong password both answer `401 INVALID_CREDENTIALS` after a full Argon2id check (`PasswordHasher.burn` for an unknown email), whatever the role. Removing the door also removes a difference: a right password at the wrong door used to cost an extra audit write before the same refusal.
- **Lockout and rate limits are unchanged:**
  - five wrong passwords lock any account for 15 minutes, and the right password does not open a locked account;
  - 10 sign-in steps per minute per address;
  - 10 attempts per 15 minutes per email.
- **The second factor is unchanged.**
  - It is decided by the role (`Role.requiresMfa`) and the user's row, never by the request.
  - With `zawed.security.mfa-required` on, a Super Admin without a second factor must enrol before any session exists, and an enrolled one must answer a TOTP challenge.
  - `DeploymentGuard` still refuses to start production with the switch off.
- **No caller can choose a role.**
  - The sign-in body is only `{ email, password }`, and unknown fields are refused with `400`.
  - Role and scope come from the user's row and are re-read on every refresh. An Ops, company or station user cannot obtain platform-admin scope.
  - Every endpoint keeps its own permission check (the authorisation matrix).
- **Sessions are unchanged:**
  - the cookie is HttpOnly, SameSite=Strict and Secure when deployed;
  - refresh rotation, reuse detection and the 60-second grace window;
  - the CSRF rule (Origin plus `X-Zawed-Client`) on every endpoint that reads the cookie;
  - the platform roles' 12-hour idle limit, which depends on the role and so still applies to the Super Admin.
- **What is given up.** The Super Admin's refresh cookie used to travel only to `/api/v1/admin/auth`, so it was isolated from the other roles' cookie. With one Super Admin per browser and the same flags on both cookies, this isolation protected nothing that the cookie flags and the CSRF rule do not already protect.

### Tests
`SignInTests` covers:
- the Super Admin at `/auth/login`, with the second-factor challenge and the enrolment;
- the same answer for every role and for an unknown email, and comparable timing;
- lockout and its audit for every role;
- no role can choose a role, a scope or a door;
- the old endpoints are gone.

`SessionTests` covers:
- the Super Admin's refresh and logout at `/auth`;
- old `ADMIN` sessions are never refreshed, and the old cookie name is not read;
- the platform idle limit.

`SignInWithoutMfaTests` covers the switch turned off. `AuthorizationMatrixTests` covers every endpoint for every role. The web has `portals.test.ts`, `session-store.test.ts` and `e2e/sign-in.spec.ts`.
