# ADR 0024: Device nonce retention

**Status:** Accepted, 2026-11-19 · **Source:** audit F5

## Context
Every device token request stores a nonce. A device in use around the clock requests a token at most every 15 minutes, which is up to 96 nonces a day. For 1,000 such devices that is about 35 M rows a year; real use (shifts, idle hours) is lower. Replay protection needs a nonce only while its timestamp is acceptable: ±5 minutes, so 10 minutes at most.

## Decision
- **Purge function:** `purge_device_nonces(before, limit)` (V14, `SECURITY DEFINER`) deletes at most `limit` nonces older than `before`. It refuses anything inside 15 minutes.
- **Job:** `network.NonceRetention` runs every 15 minutes. It passes one hour ago (`zawed.pos.nonce-retention`), deletes in batches of 5,000 (each its own short transaction), at most 40 batches a run, and logs the count.
- **Several instances:** running it on more than one instance at once only shares the work.

## Consequences
The table stays at roughly one hour of nonces, about 4,000 rows for 1,000 active devices. Replay protection is unchanged (`NonceRetentionTests`).
