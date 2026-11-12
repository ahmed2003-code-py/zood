# ADR 0010: Device Channel

**Status:** Accepted, 2026-11-12
**Source:** `docs/architecture/implementation-plan.md` §4, decision 10

## Context
PRD §6.3 requires POS device binding and instant revocation. Nagahny shipped a static shared key inside its Android app, which is a weakness. POS devices send fueling and offline batches that move money, so device requests must not be forgeable or replayable.

## Decision
- An Ops-minted activation code is single use, hashed, valid 15 minutes, with an attempt cap.
- Activation registers a device key pair.
- Device tokens carry `typ=device`; `/api/v1/pos/*` accepts only device tokens, and device tokens never pass as user tokens.
- Every batch is signed with a timestamp and a nonce.

## Consequences
- A stolen device is cut off by revoking it; its next call is refused.
- No shared secret is shipped in the app.
- The Kotlin POS must hold a private key and sign each batch; the B5 web simulator proves the protocol first.
- The server must store nonces and enforce a timestamp window to reject replays.
