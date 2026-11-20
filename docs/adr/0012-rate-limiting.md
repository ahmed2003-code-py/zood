# ADR 0012: Rate Limiting

**Status:** Accepted, 2026-11-12; amended 2026-11-20 (U1: shared limiter in PostgreSQL for the public demo form)
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

## Amendment (2026-11-20, U1): a shared limiter in PostgreSQL

### Context
- **Redis was never deployed.** Every limit still runs in `InMemoryRateLimiter`.
- **Those counters reset on every deploy and count per instance.** For the sign-in steps that is acceptable, because the lockout counters live in the database.
- **The public demo-request form has no lockout behind it.** It is anonymous, and every request it stores is a row of personal data that Operations reads. With in-memory limits:
  - a script could wait for a deploy to get a fresh budget;
  - with two instances, it would get two budgets.

### Decision
- **Where the counters live.** `platform.ratelimit.PostgresRateLimiter` keeps fixed-window counters in PostgreSQL (`rate_limit_counters`, V22). A limit therefore survives a deploy and holds across instances, with no new service to run.
- **How an attempt is counted.**
  - One upsert in `rate_limit_hit(bucket, key_hash, window_seconds)`.
  - Windows are aligned to the epoch by the database clock, so every instance agrees where a window starts.
  - Two instances counting the same key wait on the one row, and both counts land.
  - Each attempt commits in its own short transaction (`REQUIRES_NEW`). A refused or rolled-back request still spends its budget.
- **Least privilege.**
  - `rate_limit_hit` and `purge_rate_limit_counters` are `SECURITY DEFINER`, revoked from `PUBLIC` and granted to `zawed_app`, on the `purge_idempotency_keys` pattern (ADR 0022).
  - The runtime role has no grant on the table at all: it cannot read, reset or delete a counter.
  - The purge removes only windows that have ended, so it is no way to reset a limit.
- **No personal data in the table.** The subject (an address, a phone, an e-mail) is stored as a keyed HMAC (`LookupHasher`, its own key per bucket), never in clear.
- **Per-IP is allowed here.** The form is anonymous, which is the case this ADR already allows. A B2B demo request is rare per network, so 5 an hour and 20 a day per address does not lock a carrier NAT out in practice.
  - IPv6 addresses are counted by their /64, since one host can choose any address in its prefix. Each /48 also has its own budget of 20 an hour, because one customer can hold 65,536 /64s.
  - The address is `getRemoteAddr()` as the deployed profile's `forward-headers-strategy: native` reports it. Tomcat trusts `X-Forwarded-For` only from an internal proxy, never from the client.
- **Who uses it.** Only the demo form (`u1-leads-contract.md` §4):
  - per address 5 an hour and 20 a day (429);
  - per normalised phone and per e-mail 3 a day each, past which the request is set aside with a silent 202, so the answer never says a contact is known;
  - an hourly cap on the whole form (60 by default), also a silent 202 with a warning in the log. The existing per-minute limits stay in memory. Moving them is a separate decision: it adds one database round trip to every authenticated request.

### Consequences
- **Redis is no longer needed** for the demo form. Whether the per-minute limits move to PostgreSQL or to Redis later is still open.
- **Cost.** Each demo request costs up to four small upserts, and the counters table holds at most a day of windows. Ended windows are purged every hour (`zawed.rate-limit.purge-interval`, default `PT1H`).
- **Fixed windows** allow up to twice a limit across a window boundary. For a spam guard on a form, that is accepted.
