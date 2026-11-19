# ADR 0022: Request idempotency keys

**Status:** Accepted, 2026-11-19 · **Source:** audit F3

## Context
Creating a company has no natural key when there is no tax id: a retried request or a double click created a second company. Wallet adjustments have the same problem.

## Decision
- **Header:** clients may send `Idempotency-Key`, which `idempotency_keys` (V13) stores per user and operation, with the SHA-256 of the request body and the resource it created.
- **Same transaction:** the key row is written in the transaction that creates the resource. A rollback frees the key, and a concurrent request with the same key waits on the primary key, then sees the first result.
- **Replay rules:**
  - same key, same body: the same resource is returned (200);
  - same key, different body: `409 IDEMPOTENCY_KEY_REUSED`.
- **RLS:** each user sees only their own keys, so a key is never a way to read another user's resource.
- **Optional:** without the header, behaviour is unchanged. The web client sends one.

## Not done
- **Commercial registration is not made unique:** registries differ by governorate, and the owner has not confirmed a rule.
- **No retention job yet:** keys are small. A purge after 30 days can follow the nonce job's pattern.
