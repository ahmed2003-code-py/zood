# ZAWED load tests (k6)

> **Status: NOT EXECUTED.** k6 is not installed on the machine these scripts were written on, and no
> software was installed to run them. They have not been run against any environment. The numbers
> below that are not labelled as measured are arithmetic from the API's rate limits and the
> scenario stages, not measurements. The executed measurements so far are in
> [`docs/artifacts/production-hardening/performance-report.md`](../../docs/artifacts/production-hardening/performance-report.md)
> (query plans on 5 M fuelings, and concurrent online fuelings inside the test JVM).

Needs k6 v1.x (WebCrypto as the global `crypto`, used for the device's P-256 key). Run against
**staging only**: the seeding writes the synthetic devices' private keys to `fixture.json`.

| Script | What it does |
|---|---|
| `seed.js` | Builds the network and fleets through the public API and writes `fixture.json` |
| `online-fueling.js` | Online fuelings through the real POS protocol: signed device token, agent sign-in, `POST /pos/fuelings` |
| `offline-sync.js` | Offline batches (`POST /pos/sync`) of up to 100 captures, each batch sent twice (the second must be all `DUPLICATE`) |
| `portal-reads.js` | Ops and company reads with date ranges: fueling lists, activity calendar, CSV export |
| `vehicle-create.js` | Ops creates vehicles and issues their tags |
| `lib/zawed.js` | Shared code: sign-in with TOTP, the device channel, the deterministic fleet |

## The protocol the scripts follow

From `docs/architecture/b4-fueling-contract.md`, `b5-sync-contract.md` and `apps/web/src/pos/live/pos-api.ts`:

1. Ops registers a device (`POST /ops/devices`) and gets a one-time activation code.
2. The device makes an EC P-256 key pair and activates: `POST /pos/activate {deviceCode, activationCode, publicKey}` (SPKI, base64).
3. For a device token (15 minutes) it signs `zawed-device-token\n{deviceId}\n{timestamp}\n{nonce}` with ECDSA P-256/SHA-256. WebCrypto returns `r‖s`; the API verifies DER, so the script converts (`rawToDer`, same as `device-key.ts`). Each nonce is used once.
4. The agent signs in (`POST /pos/agents/login`, Worker ID + PIN) and gets `X-Agent-Session`. A new agent must change the PIN first; `seed.js` does it.
5. `POST /pos/fuelings {clientTxnId, tagUid, volumeMl, deviceAt, receiptLocale}` with the device token, the agent session and `X-Zawed-Contract: 1`.

## Limits that shape the tests

| Limit (from the code) | Consequence |
|---|---|
| A tag fuels once per company cooldown (120–240 min) | Every fueling in a run needs a car not used before: **tags ≥ total fuelings of the run** |
| 120 POS requests a minute per device | Plan 1 fueling a second per device: **devices ≥ peak rate** (×1.5 for the burst stage) |
| 10 activations a minute per address | `seed.js` paces activations at one per 6.5 s |
| 300 calls a minute per signed-in user | `seed.js` and `vehicle-create.js` pace each Ops user at under 5 calls a second and use several Ops users in parallel (`OPS_USERS`) |
| 30 sync batches a minute per device | `offline-sync.js` sends 2 batches per iteration (original and resend) |
| 10,000 EGP offline per device and company per 24 h | Past it, captures are flagged and their payable held; spread offline captures over companies |

## Seeding

Create Ops users with TOTP enrolled (and optionally a Super Admin for the portal users) in staging,
and list the Ops users in `ops-users.json` (`[{"email": "...", "password": "...", "totpSecret": "BASE32..."}]`).
`fixture.json` and `ops-users.json` are ignored by git.

```
k6 run -e BASE_URL=https://staging.example -e OPS_USERS=./ops-users.json \
       -e ADMIN_EMAIL=... -e ADMIN_PASSWORD=... -e ADMIN_TOTP=... \
       -e RUN=1 -e COMPANIES=10 -e VEHICLES_PER_COMPANY=450 seed.js
```

Plates and tag UIDs are computed from `(RUN, company, vehicle)`; use a new `RUN` (1–255) for every seeding.

| Profile | `COMPANIES` | `VEHICLES_PER_COMPANY` | Tags | Devices (`OPERATORS`×`STATIONS_PER_OPERATOR`×`DEVICES_PER_STATION`) | Supports an online run at | Vehicle seeding calls | Vehicle seeding time with 1 / 10 Ops users (from the 300/min limit) |
|---|---:|---:|---:|---|---|---:|---|
| Small | 10 | 450 | 4,500 | 5×4×3 = 60 | `RATE=5` | 9,000 | ~31 min / ~3 min |
| Medium | 30 | 450 | 13,500 | 5×4×3 = 60 | `RATE=15` | 27,000 | ~94 min / ~10 min |
| Target | 100 | 450 | 45,000 | 5×4×4 = 80 | `RATE=50` | 90,000 | ~5.2 h / ~31 min |

Setup before the vehicles (network, activations, companies, credits, portal users) adds about one
activation every 6.5 s plus 0.21 s per other call: roughly 8–12 minutes for every profile.

Total fuelings of one `online-fueling.js` run with the default stages, as a function of `RATE`:
1 min to RATE/5, 2 min to RATE, 10 min at RATE, 2 min to 1.5×RATE, 1 min to 0, which is
about 874 × `RATE` fuelings (43,700 at `RATE=50`). Use `CAR_OFFSET` to start a second run inside the
cooldown on cars the first did not use.

## Scenarios and stages

| Scenario | Executor and stages | Load |
|---|---|---|
| Online fueling | `ramping-arrival-rate`: 1 min to RATE/5 (warm-up), 2 min ramp to RATE, `HOLD` (10 min) at RATE, 2 min to 1.5×RATE, 1 min down | One VU per device; each fueling on a fresh car, cars spread round-robin over all companies |
| Offline sync | `ramping-vus`: 2 min ramp to `VUS` (20), `HOLD` (5 min), 1 min down | Each VU = one device; batches of `BATCH` (50) captures dated in the last minutes, each sent twice |
| Portal reads | `constant-arrival-rate`: Ops 4/s (one Ops user), company 10/s (spread over Fleet Managers), for `HOLD` | Lists newest-first with and without `from`/`to` (last 30 days, a month 6 months back), activity for a month, CSV export for the last month |
| Vehicle creation | `constant-arrival-rate`: 2 vehicles/s per Ops user, for `HOLD` (5 min) | Vehicle plus tag |
| Mixed (recommended) | Run `online-fueling.js` and `portal-reads.js` at the same time, then add `offline-sync.js` | Reads over history while the wallet locks are busy |

Before a run that is meant to judge the database read paths, load history first (the reads are fast
on an empty table): restore a copy of a staging database that has months of fuelings, or generate
them with the SQL in `apps/api/src/test/java/com/zawed/api/perf/QueryPlanReport.java` (owner access,
staging only).

## Acceptance targets: PROPOSED

These are **PROPOSED** targets for the owner to approve. None has been measured with k6.

| Target | Threshold in the scripts |
|---|---|
| Online fueling p95 < 300 ms at 50 req/s sustained | `http_req_duration{name:pos/fuelings}: p(95)<300` |
| Error rate < 0.1 % | `http_req_failed{name:pos/fuelings}: rate<0.001` |
| Fuelings completed (no unexpected decline) > 99.9 % | `fueling_completed: rate>0.999` |
| Zero deadlocks | Not visible to k6: compare `select deadlocks from pg_stat_database where datname = current_database()` before and after the run; the target is no increase |
| Wallets and payables reconcile after the run | Run the nightly reconciliation (or its query) after the run; the target is zero mismatches |
| Offline batch of 50 p95 < 5 s, no `RETRY`/`REJECTED` | `offline-sync.js` thresholds |
| Lists p95 < 500 ms, activity < 1 s, export < 3 s | `portal-reads.js` thresholds |
| Vehicle create and tag issue p95 < 500 ms | `vehicle-create.js` thresholds |

Also watch during the run: HikariCP pool usage (default maximum 10 connections), database CPU, and
lock waits (`pg_stat_activity` where `wait_event_type = 'Lock'`).
