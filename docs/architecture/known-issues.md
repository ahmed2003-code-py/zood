# Known issues

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
