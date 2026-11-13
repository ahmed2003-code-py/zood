# B1 — Identity and access: contract

The agreement the API (`apps/api`, module `identity`) and the web (`apps/web`) are built against
in parallel. A change to a path, a field or an error code here is made in this file first.

**Sources:** PRD §3, §6.2–6.4, §10.2 · ADR 0008 (authentication), 0009 (MFA), 0012 (rate limiting) ·
plan §3.6 (RLS), §6 (security baseline) · patterns from Nagahny (`D:\my startups\darsly\apps\api\src\auth\*`).

## 1. Topology: one origin

The browser talks to **one origin**. In development Vite proxies `/api`; on Railway the web service
serves the built app and reverse-proxies `/api/*` to the API over Railway's private network. So:

- No CORS is needed for the web app (CORS stays only for tools).
- The refresh cookie can be `SameSite=Strict`.
- `VITE_API_BASE_URL` stays unset; the client calls `/api/v1/...`.

## 2. Roles and scope

| Role | Code | Scope | Signs in at | MFA |
|---|---|---|---|---|
| Super Admin | `SUPER_ADMIN` | platform | `/admin` (separate login) | required (TOTP) |
| Operations | `OPS` | platform | `/ops` | required (TOTP) |
| Fleet Manager | `FLEET_MANAGER` | one company | `/company` | not in B1 |
| Accountant | `ACCOUNTANT` | one company | `/company` | not in B1 |
| Station Manager | `STATION_MANAGER` | one station operator | `/station` | not in B1 (B6) |

Station agents (Worker ID + PIN on the POS) and drivers are **not** B1: agents arrive with the device channel (B5).

`scope` is `{ "type": "PLATFORM" }`, `{ "type": "COMPANY", "id": "<uuid>" }` or `{ "type": "OPERATOR", "id": "<uuid>" }`.
Companies and operators are created in B2; until then a scoped user's scope id is not checked against a table.

User status: `ACTIVE`, `SUSPENDED`, `DEACTIVATED` (never deleted, PRD §3).

## 3. Tokens and session

- **Access token:** JWT (HS256, `ZAWED_JWT_SECRET`, ≥ 32 bytes), 15 minutes, claims `sub`, `sid` (session id), `role`, `scope`, `typ: "user"`. Sent as `Authorization: Bearer`. Kept **in memory only** by the web (never `localStorage`).
- **Every request checks the session row:** revoked session, suspended or deactivated user → 401 immediately, even with a valid token.
- **Refresh token:** opaque random 32 bytes, stored as SHA-256 hash, rotated on every refresh, previous hash kept for a 60-second race grace window; any other reuse revokes the whole session (`REFRESH_REUSE_DETECTED`). Lifetime 12 hours for platform roles, 7 days for scoped roles; absolute session cap 30 days.
- **Cookie:** `zawed_rt`, `HttpOnly`, `Secure` (deployed), `SameSite=Strict`, `Path=/api/v1/auth`. Plus a readable `zawed_session=1` hint cookie (`Path=/`) so the web knows whether to try a refresh on load.
- **CSRF:** `refresh` and `logout` require the `Origin` header to be an allowed origin and the header `X-Zawed-Client: web`.
- Role, status and scope are **re-read from the database on every refresh**.

## 4. Endpoints

All under `/api/v1`. Bodies are JSON. Every failure is the `ApiError` envelope.

### Public (no access token)

| Method | Path | Body | Success |
|---|---|---|---|
| POST | `/auth/login` | `{ email, password }` | `200 LoginResult` |
| POST | `/auth/mfa/verify` | `{ challengeToken, code }` | `200 LoginResult` |
| POST | `/auth/mfa/enroll/start` | `{ challengeToken }` | `200 { secret, otpauthUri }` |
| POST | `/auth/mfa/enroll/confirm` | `{ challengeToken, code }` | `200 LoginResult` |
| POST | `/auth/refresh` | — (cookie) | `200 Session` + rotated cookie |
| POST | `/auth/logout` | — (cookie) | `204`, cookies cleared, session revoked |
| POST | `/setup/super-admin` | `{ setupToken, email, fullName, password }` | `201` — only while no Super Admin exists and `ZAWED_BOOTSTRAP_TOKEN` is set; constant-time token compare |

`LoginResult` is one of:

```json
{ "status": "AUTHENTICATED", "session": Session }
{ "status": "MFA_REQUIRED", "challengeToken": "…", "expiresInSeconds": 300 }
{ "status": "MFA_ENROLLMENT_REQUIRED", "challengeToken": "…", "expiresInSeconds": 600 }
```

`Session`:

```json
{
  "accessToken": "…",
  "expiresInSeconds": 900,
  "user": {
    "id": "uuid", "email": "…", "fullName": "…", "role": "OPS",
    "scope": { "type": "PLATFORM" },
    "mustChangePassword": false, "mfaEnabled": true
  }
}
```

`/auth/login` with `SUPER_ADMIN` credentials is refused (`401 INVALID_CREDENTIALS`, same as a wrong password); Super Admins use `/admin/auth/login`, same body and result. The admin cookie is separate: `zawed_admin_rt`, `Path=/api/v1/admin/auth`, refreshed at `/admin/auth/refresh`, logout at `/admin/auth/logout`.

### Signed in (any role)

| Method | Path | Body | Success |
|---|---|---|---|
| GET | `/auth/me` | — | `200 user` (same shape as `Session.user`) |
| POST | `/auth/password` | `{ currentPassword, newPassword }` | `204`; revokes all other sessions of the user |
| GET | `/auth/sessions` | — | `200 [{ id, current, createdAt, lastSeenAt, userAgent, ipPrefix }]` |
| DELETE | `/auth/sessions/{id}` | — | `204` (own sessions only, else 404) |

While `mustChangePassword` is true, every endpoint except `/auth/me`, `/auth/password`, `/auth/logout` answers `403 PASSWORD_CHANGE_REQUIRED`.

### Super Admin

| Method | Path | Body | Success |
|---|---|---|---|
| GET | `/admin/users?role=&status=&q=&cursor=&limit=` | — | `200 Page<User>` |
| POST | `/admin/users` | `{ email, fullName, role, scope }` | `201 { user, temporaryPassword }` — the password is shown once |
| POST | `/admin/users/{id}/reset-password` | — | `200 { temporaryPassword }`; sets `mustChangePassword`, revokes sessions |
| POST | `/admin/users/{id}/unlock` | — | `204` |
| POST | `/admin/users/{id}/status` | `{ status, reason }` | `204`; revokes sessions when not `ACTIVE` |
| POST | `/admin/users/{id}/mfa/reset` | `{ reason }` | `204`; next login re-enrols |
| GET | `/admin/audit?actorId=&entityType=&entityId=&from=&to=&cursor=&limit=` | — | `200 Page<AuditEntry>` |

Ops in B1 may: `GET /admin/users` filtered to scoped roles, `reset-password` and `unlock` for scoped roles only. Creating users of scoped roles moves to company/operator onboarding in B2. A Super Admin cannot change their own role or status, and the last active Super Admin cannot be suspended.

`Page<T>` = `{ "items": [T], "nextCursor": "…" | null }`, `limit` 1–100, default 25.

## 5. Error codes

| Code | Status | When |
|---|---|---|
| `INVALID_CREDENTIALS` | 401 | wrong email or password, unknown email, wrong door (admin on `/auth/login`). Identical body and similar timing in every case. |
| `ACCOUNT_LOCKED` | 423 | after 5 failed attempts for 15 minutes; `params.retryAfterSeconds` |
| `ACCOUNT_DISABLED` | 403 | suspended or deactivated — returned only **after** a correct password |
| `MFA_INVALID_CODE` | 401 | wrong or replayed TOTP code |
| `MFA_CHALLENGE_EXPIRED` | 401 | challenge token expired or used |
| `SESSION_EXPIRED` | 401 | refresh token missing, expired, or the session was revoked |
| `REFRESH_REUSE_DETECTED` | 401 | a rotated-away refresh token was presented; the session is revoked |
| `PASSWORD_CHANGE_REQUIRED` | 403 | see above |
| `PASSWORD_TOO_WEAK` | 400 | `field: "newPassword"`, `params.minLength` |
| `PASSWORD_REUSED` | 400 | new password equals the current one |
| `CSRF_REJECTED` | 403 | missing/foreign `Origin` or missing `X-Zawed-Client` on refresh/logout |
| `SETUP_NOT_AVAILABLE` | 404 | bootstrap disabled or a Super Admin already exists |
| `EMAIL_IN_USE` | 409 | |
| `LAST_SUPER_ADMIN` | 409 | |
| `RATE_LIMITED` | 429 | `retryAfterSeconds` and `Retry-After` header |
| `UNAUTHENTICATED` | 401 | no or invalid access token |
| `FORBIDDEN` | 403 | role lacks the permission |
| `NOT_FOUND` | 404 | includes rows outside the caller's scope |
| `SELF_CHANGE_NOT_ALLOWED` | 409 | a Super Admin changing their own status |
| `SCOPE_MISMATCH` | 400 | `field: "scope"`; the role needs a different kind of scope |
| `INVALID_CURSOR` | 400 | `field: "cursor"` |

`POST /auth/password` with a wrong current password answers `401 INVALID_CREDENTIALS` with `field: "currentPassword"`; the web shows its own sentence for it.

## 7. Notes from the implementation (2026-11-13)

- The per-email sign-in limit is **10 per 15 minutes** (not 5): the account lockout already stops at 5 wrong passwords, and the email limit exists to slow probing of addresses that have no account.
- A password change is limited to 10 attempts per 15 minutes per user, so a stolen access token cannot brute-force the current password.
- Method security runs after request validation: a caller without the permission who sends an invalid body gets `400`, not `403`. The authorization matrix test sends valid bodies for that reason.
- Database layer: the application connects as the owner and switches every pooled connection to the `NOLOGIN` role `zawed_app` (`SET ROLE`). Row-level security applies to `users` and `audit_log`; the per-transaction scope is set in one place (`ScopedTransactionManager`). The sign-in path uses `SECURITY DEFINER` functions instead of switching row-level security off.

## 6. Limits

| What | Limit |
|---|---|
| Login, MFA verify, enrolment, setup | 10 per minute per IP, and 5 per 15 minutes per email |
| Refresh | 30 per minute per session |
| Any authenticated request | 300 per minute per user |
| Password: length | 12–128 characters, not containing the email's local part |
| TOTP | RFC 6238, SHA-1, 6 digits, 30 s, ±1 step, a used step cannot be reused |
