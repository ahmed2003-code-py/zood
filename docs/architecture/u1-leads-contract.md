# U1 contract: demo-request leads

**Status:** implemented on `feature/u1-leads-api`, 2026-11-20 · **Schema:** `V22__leads_and_shared_rate_limits.sql`
**Builds on:** `b1-identity-contract.md` (error envelope, roles, scopes), `database-erd.md` §3 (multi-tenancy). **ADR:** 0012 (rate limiting, amended for U1). **Retention:** §6 below, also in ADR 0028.

The landing page's "Request a demo" form (`HANDOVER.md` §4) sends a request to the API. The request becomes a **lead** in the Ops console, which only platform users can see.

## 1. Endpoints

| Method and path | Who | Answer |
|---|---|---|
| `POST /api/v1/public/demo-requests` | anyone; no token, no cookie, no session | `202 {"status":"RECEIVED"}` |
| `GET /api/v1/ops/leads?status=&cursor=&limit=` | `SUPER_ADMIN`, `OPS_MANAGER`, `OPS` (`LEADS_MANAGE`) | `{ items: Lead[], nextCursor }` |
| `PATCH /api/v1/ops/leads/{id}` | the same | the updated `Lead` |

Every refusal uses the API's `ApiError` envelope (`code`, `message`, `fields`, `requestId`, …). Company and station roles get `403 FORBIDDEN` on the Ops routes, the same for a real lead id as for a missing one. An unknown lead answers `404 NOT_FOUND` to an Ops user.

**Security chain.** The public path is one more entry in `SecurityConfig.PUBLIC_POST`.
- The chain is stateless, and CSRF protection is already off for every route: access tokens travel in the `Authorization` header, which a browser never attaches by itself. Only refresh and logout read a cookie, and they check `Origin` themselves. No other route changed.
- The form sends no credential, so there is nothing a cross-site request could ride on.
- A cross-site page cannot send `application/json` without a CORS preflight, and the preflight fails for origins outside `zawed.cors.allowed-origins`.

## 2. `POST /api/v1/public/demo-requests`

```json
{
  "companyName": "شركة النيل للنقل",
  "contactName": "منى عادل",
  "phone": "0100 123 4567",
  "email": "mona@example.com",
  "fleetSize": "20-49",
  "locale": "ar",
  "website": "",
  "startedAt": 1763632800000
}
```

| Field | Rule after cleaning | Field error `code` |
|---|---|---|
| `companyName` | 2–120 characters, plain text | `NOT_BLANK`, `SIZE`, `PATTERN` |
| `contactName` | 2–80 characters, plain text | `NOT_BLANK`, `SIZE`, `PATTERN` |
| `phone` | an Egyptian mobile: `01` + `0`/`1`/`2`/`5` + 8 digits | `NOT_BLANK`, `PATTERN` |
| `email` | at most 254 characters, `local@domain.tld`, no whitespace anywhere (Unicode spaces such as U+00A0 and U+3000 included), plain text | `NOT_BLANK`, `SIZE`, `EMAIL`, `PATTERN` |
| `fleetSize` | `1-19`, `20-49`, `50-99`, `100-249` or `250+` | `NOT_NULL`, `PATTERN` |
| `locale` | `ar` or `en` | `NOT_NULL`, `PATTERN` |
| `website` | the honeypot: send it empty | — |
| `startedAt` | when the form was shown, epoch milliseconds | `NOT_NULL` |

**Cleaning, before validation** (`leads/DemoRequestText.java`):
- **Names:** trimmed, and every run of whitespace becomes one space. That includes tabs, line breaks and no-break spaces.
- **Plain text** means no character of Unicode category C, except the zero-width non-joiner U+200C, which shapes Arabic-script text. Category C covers:
  - control characters that are not whitespace;
  - format characters: zero-width spaces and joiners, LRM, RLM and ALM (U+061C), the bidirectional embeddings, overrides and isolates, word joiners (U+2060–U+2064), the byte-order mark, the soft hyphen and tags;
  - private-use, unassigned and lone surrogate code points.

  They are invisible, or make a name read differently in the console than what was stored.
- **Phone:**
  - Arabic-Indic and Persian digits become ASCII.
  - Spaces, dashes, dots and brackets are removed.
  - `+20`, `0020`, a bare `20` before 10 digits, or a missing leading `0` become the leading `0`.
  - It is stored as `01XXXXXXXXX`.
  - Anything else stays invalid.
- **E-mail:** trimmed and lower-cased.

**Answers:**

| Case | Status | Body |
|---|---|---|
| Stored as a new lead | 202 | `{"status":"RECEIVED"}` |
| A repeat (§3) | 202 | the same |
| Set aside: honeypot filled, or the time trap (§3) | 202 | the same; no row |
| A field fails | 400 | `code: VALIDATION_FAILED`, `fields: [{field, code, message}]` |
| Unknown field, broken JSON, wrong type | 400 | `code: BAD_REQUEST` (no `fields`), as on every endpoint |
| Body over 4 KiB (4,096 bytes), with or without `Content-Length` | 413 | `code: PAYLOAD_TOO_LARGE`, `params.maxBytes: 4096` |
| Set aside: past the per-contact limit or the hourly cap (§4) | 202 | the same; no row |
| A per-address limit is spent (§4) | 429 | `code: RATE_LIMITED`, `retryAfterSeconds`, and a `Retry-After` header (exposed to the browser by CORS) |

## 3. Spam protection and repeats

- **Honeypot.** If `website` holds anything but whitespace, the request is answered with the same silent `202 {"status":"RECEIVED"}` as a stored one, and dropped: no row, no error, nothing in the answer that tells it apart. The field is not validated, so whatever a bot puts there never changes the answer, as long as the other fields are valid.
  - The web form must hide the field from people, keep it out of the tab order (`tabindex="-1"`, `aria-hidden`) and switch off autofill (`autocomplete="off"`). Some browsers autofill a field named "website".
- **Time trap.** The request is answered 202 and dropped when `startedAt` is:
  - less than 3 seconds before the server's time (`too_fast`);
  - after it (`future`);
  - more than 24 hours before it (`stale`).
- **Repeats.** A request with the same phone **or** the same e-mail as a lead last requested within the past 24 hours creates no new lead.
  - It moves that lead's `last_requested_at` forward and adds one to `request_count`.
  - What the prospect first sent, and the lead's status, stay as they are.
  - The answer is the same 202, so the response body and status never tell whether a phone or e-mail is already known. (Timing can differ: a request that is set aside answers sooner than one that is stored. That is accepted.)
  - When a repeat names another company, contact, phone, e-mail or fleet size than the stored lead, `differingRequests` goes up by one. The stored details stay the first request's. Ops sees both counts, so a lead whose phone or e-mail someone else used is visible as such. Nothing about the repeat is logged.
  - Two identical requests at the same moment make one lead: `submit_lead` takes a transaction-level advisory lock.
- **Metric.** Every outcome is counted in `zawed.leads.demo.requests` with the tag `outcome`:
  - stored: `created`, `repeated`;
  - dropped: `honeypot`, `too_fast`, `future`, `stale`, `rate_limited_contact`, `global_cap`;
  - refused (429): `rate_limited_address`.
  - An alert on `global_cap` (and on a rising `honeypot`) is the signal that the form is under attack; `global_cap` also logs a warning, without any data.

## 4. Rate limits

| Bucket | Limit | Key | Checked |
|---|---|---|---|
| `demo-ip-hour` | 5 an hour | the client address | before the body is read: invalid, oversized and dropped requests count too |
| `demo-ip-day` | 20 a day | the client address | the same |
| `demo-ip48-hour` | 20 an hour | an IPv6 client's /48 (65,536 /64s) | the same |
| `demo-phone-day` | 3 a day | the normalised phone | after validation and the spam checks; past it **a silent 202** |
| `demo-email-day` | 3 a day | the normalised e-mail | the same |
| `demo-all-hour` | 60 an hour (`zawed.leads.max-per-hour`) | the whole form | last, before the lead is stored; past it a silent 202, counted as `global_cap`, and a warning in the log |

- **Why the per-contact limit answers 202 (review of U1).** A 429 on someone's first try would tell them the phone or e-mail had already been used three times today.
  - The cost: someone who knows a prospect's phone can make that prospect's own requests that day go unrecorded.
  - What limits the damage: the lead already exists, its `requestCount` and `differingRequests` show the repeats, and Ops calls the number anyway.
- **The hourly cap** bounds what a botnet, or an IPv6 /48 spread over many addresses, can put in front of Ops. The per-address limits alone do not.

- **Client address:** `request.getRemoteAddr()`, as everywhere in the API.
  - In the `deployed` profile, `server.forward-headers-strategy: native` lets Tomcat take the address from `X-Forwarded-For` only when the request comes from an internal proxy (Railway's). A header the client adds itself is ignored, and a test checks this.
  - IPv6 addresses are counted by their /64 (`ClientInfo.limitKey`), and each /48 has its own wider budget (`ClientInfo.networkKey`).
  - `DemoRequestBehindProxyTests` runs with `forward-headers-strategy=native`, as deployed. A forged entry to the left of the proxy's own is ignored, and a positive control shows the header is really read.
  - **To measure on zawed-dev:** whether Railway's edge appends to `X-Forwarded-For` (rather than replacing it), and whether it connects from an address Tomcat counts as an internal proxy (10/8, 172.16/12, 192.168/16, 169.254/16, 127/8, 100.64/10). If it does not, `remote-ip` must be configured, or every visitor shares one address.
- **Path spellings.** `CanonicalPathFilter` (B7) refuses every non-canonical spelling, such as `demo%2Drequests`, with `400 PATH_NOT_CANONICAL` before anything else runs.
  - The gate matches with Spring Security's `PathPatternRequestMatcher`, on decoded segments as Spring routes it, never by comparing the raw URI.
  - It runs right after that filter and before the security chain.
  - Each request it let through is marked. A demo request that reaches the controller without the mark is refused with 400, so a spelling the gate misses fails closed.
- **Windows** are fixed, aligned to the epoch by the database clock. A day runs from 00:00 UTC.
- **Storage:** the counters live in PostgreSQL (§5), so they survive a deploy and are shared by every instance.

## 5. Data (V22)

- **`leads`** has these columns: id, the reference `L-10001`…, company and contact name, phone, e-mail, fleet size, locale, status (`NEW`, `CONTACTED`, `QUALIFIED`, `CLOSED`), note (at most 1,000 characters), `request_count`, `differing_requests`, `last_requested_at`, created, updated, `updated_by` and `closed_at`.
- **Writing a lead:** the runtime role has no `INSERT` and no `DELETE` on `leads`.
  - The public path has no principal and no scope. It writes through `submit_lead(...)`, a `SECURITY DEFINER` function with fixed columns: status `NEW`, no note.
  - `zawed_app` may update only `status`, `note`, `updated_at`, `updated_by` and `closed_at`. What the prospect sent can never be changed through the application.
- **Row-level security:** a row is visible and changeable only when `app_is_platform() AND app_user_id() IS NOT NULL`.
  - That excludes company and operator scopes.
  - It also excludes every POS route, which runs in the platform scope with no user.
  - An update must be written in the acting user's name (`updated_by = app_user_id()`).
- **`rate_limit_counters`** holds a bucket, the HMAC (`LookupHasher`) of the key, the window and the hits.
  - The runtime role has no grant on it at all. It counts only through `rate_limit_hit(bucket, key_hash, window_seconds)`.
  - Ended windows are deleted through `purge_rate_limit_counters(limit)`, every hour, by `RateLimitCounterRetention` (recorded in `job_runs` as `rate-limit-retention`).
  - Both functions are `SECURITY DEFINER`, revoked from `PUBLIC` and granted to `zawed_app`, on the `purge_idempotency_keys` pattern.
  - No function can remove a window that is still running.

## 6. Personal data and retention

- **Logs:** a lead's name, phone and e-mail never appear in a log line, an audit row, an error or a metric. Logs carry only the lead's id and reference.
  - A database error on the insert is logged by SQL state only, because PostgreSQL's message can quote the failing row.
  - A test captures the output and checks this.
- **Audit:** `LEAD_UPDATED`, written in the same transaction as the change, on entity `LEAD` with the lead's id. The metadata is `{reference, from, to, noteChanged}`; the note itself is never in the audit row.
- **No third-party calls.** Nothing is sent to a CRM, an e-mail service or analytics. Sentry already drops request bodies (`SentryScrubber`).
- **Retention (ADR 0028):**
  - A `CLOSED` lead is deleted **24 months after it was closed** (`zawed.leads.closed-retention`, default `P730D`). `LeadRetention` runs daily through `purge_closed_leads(before, limit)`, in bounded batches, and records each run in `job_runs` (`lead-retention`).
  - The function refuses any cutoff inside **90 days**, whoever calls it.
  - Reopening a lead clears `closed_at` and stops the clock.
  - Open leads are kept, because they are Operations' work in progress (owner question 2 below).
  - The `LEAD_UPDATED` audit rows outlive the lead. They hold no personal data.
  - Rate-limit counters are deleted once their window ends: at most a day for these buckets.

## 7. Ops endpoints

**`GET /api/v1/ops/leads`:**
- Leads come newest first, by `created_at` and then `id`.
- `cursor` is the last item's `id`, as on every other list. The next page starts strictly before that lead's `(created_at, id)`.
- `limit` is optional, with the same bounds as every Ops list: 25 by default, clamped to 1–100 (`limit=0` gives 1, `limit=500` gives 100).
- `status` filters by one status.
- A cursor that is not an id answers `400 INVALID_CURSOR`.

```json
{ "items": [ { "id": "…", "reference": "L-10001", "companyName": "…", "contactName": "…",
  "phone": "01001234567", "email": "mona@example.com", "fleetSize": "20-49", "locale": "ar",
  "status": "NEW", "note": null, "createdAt": "…", "updatedAt": "…",
  "requestCount": 1, "lastRequestedAt": "…", "differingRequests": 0 } ], "nextCursor": "…" }
```

`requestCount`, `lastRequestedAt` and `differingRequests` were added after the review of U1. They are additive: a client that ignores them is unaffected.

**`PATCH /api/v1/ops/leads/{id}`** takes `{ "status": "CONTACTED", "note": "…" }`:
- **`status`** is required, and any status may follow any other, so Ops can correct a mistake.
- **`note`** is optional:
  - omitted or `null`: unchanged;
  - `""` or blank: removed, stored as `null` (the web clears a note with `""`);
  - otherwise at most 1,000 characters, with line breaks kept and control characters refused (`PATTERN`).
- **Closing** sets `closed_at`. **Reopening** clears it.
- **The answer** is the lead, in the same shape as a list item.

## 8. Tests

| Property | Test |
|---|---|
| Cleaning, phone formats, plain text (all of category C except ZWNJ, long values), time trap | `leads/DemoRequestTextTests` |
| Encoded path spellings, the handler-side check | `leads/DemoRequestGateTests` |
| Forged `X-Forwarded-For` behind a proxy, with forwarded headers on as deployed | `leads/DemoRequestBehindProxyTests` |
| IPv6 /64 address key and /48 network key | `platform/web/ClientInfoTests` |
| 202 and the stored values, validation codes (Unicode spaces in an e-mail), 400/413, honeypot and time trap (no row, metric), repeats and `differingRequests`, concurrent duplicates, per-address limits (also on an encoded path), silent per-contact limits, the hourly cap, survival of an in-memory reset, no personal data in the log | `leads/DemoRequestTests` |
| Ops list keyset paging, `limit` bounds and the status filter; PATCH (an empty-string note clears it) and its audit; 404 | `leads/LeadsOpsTests` |
| RLS per scope (no scope, platform user, platform without user, company, operator), no INSERT, DELETE or rewrite, update only in one's own name, retention and its floor | `leads/LeadsIsolationTests` |
| Two instances share a budget under concurrency, a restart keeps counting, windows, hashing, purge, no table access | `platform/ratelimit/PostgresRateLimiterTests` |
| The functions with an ordinary (non-superuser) owner | `identity/NonSuperuserOwnerTests` |
| Role × endpoint | `identity/AuthorizationMatrixTests` |
| Public form exempt (takes no id), Ops routes platform-only, lead data among B's markers | `fueling/IdorSweepTests` |

## 9. Open questions

1. **Client clocks.** `startedAt` is the browser's clock. A visitor whose clock runs ahead of the server's by more than the time they took to fill the form is dropped as `future` or `too_fast`. Phones and laptops normally sync their clocks, so this is rare. A sturdier contract would send the elapsed time (`performance.now()` deltas) instead. It is not changed here, because the web is built against `startedAt`.
2. **Open leads are never purged.** Should a lead untouched for, say, 12 months be closed or deleted automatically?
3. **Per-contact lockout.** Answering the per-contact limit with a silent 202 hides whether a contact is known, but a third party who knows a prospect's phone can make that prospect's own requests go unrecorded for a day (§4). Is that trade acceptable?
4. **Notification.** Ops learns of a new lead only by opening the list. An e-mail or WhatsApp alert would be a third-party call, so it needs a decision.
