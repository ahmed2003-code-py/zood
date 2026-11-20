# Known issues

## A top-up request tells a company that a bank reference is already claimed

**Status:** open, owner decision needed. Low severity. Found by the IDOR sweep (B7 W2, 2026-11-19).

**Behaviour:** `POST /company/topups` with a bank reference that another company has already claimed answers `409 BANK_REFERENCE_IN_USE`. A reference nobody claimed answers `201`. So a company can test whether some live top-up on ZAWED's receiving account carries a given reference. The answer never says whose it is, how much or when, and no data of the other company is returned.

**Why it is so:** one bank transfer must credit once (B3, ADR 0021). The partial unique index `topup_requests_reference_live_key` covers `PENDING`, `PENDING_APPROVAL` and `CONFIRMED` across all companies.

**Options for the owner:**
1. Accept it as is, because references are hard to guess.
2. Let a second company's claim stand as `PENDING`, keep the platform-wide check at verification, and let Operations settle the clash. This needs a migration (V19) and a rule for who sees the clash.

`IdorSweepTests` records the current answer as a reviewed contract case (`Kind.CONTRACT`), so a change shows up there.

## Intermittent `net::ERR_NETWORK_ACCESS_DENIED` in local Playwright runs

**Status:** open, not a product defect. Does not block demo phases.
**Seen:** 2026-10-12, D0, on the main development machine (Windows 11).

**Symptom:** during `npm run e2e`, Chromium randomly fails individual requests to the local
server (`http://127.0.0.1:5180`) with `net::ERR_NETWORK_ACCESS_DENIED`: sometimes the page
itself, sometimes a stylesheet or font. A test whose stylesheet failed then sees layout
assertions fail (e.g. the desktop table visible at 390px), which is a consequence of the missing
CSS, not a layout bug.

**What was ruled out:** the application (every failure traced to a denied request), the command
sandbox (reproduced unsandboxed), parallelism (reproduced with one worker), and the dev server
(reproduced against `vite preview` of the production build).

**Likely cause:** local security software or firewall policy on loopback connections.

**Mitigation:** Playwright runs against the production build with `retries: 2`. Failures that
are only `ERR_NETWORK_ACCESS_DENIED` can be treated as environmental; any other failure is real.
Re-check on CI or another machine before relying on E2E results as a release gate.
