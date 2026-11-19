# ADR 0022: Request idempotency keys

**Status:** Accepted, 2026-11-19; amended 2026-11-19 (B7 W1: mandatory on wallet adjustments, retention) · **Source:** audit F3, hardening review (final handover, limitation 1)

## Context
Creating a company has no natural key when there is no tax id: a retried request or a double click created a second company. Wallet adjustments have the same problem.

## Decision
- **Header:** clients send `Idempotency-Key`, which `idempotency_keys` (V13) stores per user and operation, with the SHA-256 of the request body and the resource it created.
- **Same transaction:** the key row is written in the transaction that creates the resource. A rollback frees the key, and a concurrent request with the same key waits on the primary key, then sees the first result.
- **Replay rules:**
  - same key, same body: the same resource is returned (200 for a company; a wallet adjustment answers 201 with the same id);
  - same key, different body: `409 IDEMPOTENCY_KEY_REUSED`.
- **RLS:** each user reads and inserts only their own keys, so a key is never a way to read another user's resource. Nobody updates a key. Only the purge job deletes one (below).
- **Where the key is required (amended).** It is required where a duplicate would move money and nothing else identifies a retry:
  - **Wallet adjustments** (`POST /ops/companies/{id}/wallet/adjustments`). Without a key, a retry was a second pending adjustment, an approver could approve both, and the wallet was credited twice. A missing or blank key answers `400 INVALID_IDEMPOTENCY_KEY` with `field: "Idempotency-Key"`. The check runs in the service, after the permission check, so a caller without `WALLET_CREDIT` still gets `403`.
  - **Company creation stays optional:** a duplicate company moves no money. The web client sends a key anyway.
  - **Endpoints that do not need one:**
    - Recording a bank transfer is keyed by the transfer itself: its reference is unique per receiving account among live top-ups, and a conflict replays the first record.
    - Verifying, approving, rejecting and cancelling act on an existing id under its row lock: a repeated approval answers the confirmed row, and the others answer `409` once decided.
- **Retention (amended, V17).**
  - **Who deletes:** `IdempotencyKeyRetention` purges keys older than `zawed.idempotency.key-retention`, every `zawed.idempotency.purge-interval` (default `PT6H`). It deletes in batches of 5,000 and at most 40 batches per run, each batch its own transaction, and logs the count. The job runs in the platform scope with no user, a reviewed `DbScope.runAs` escalation listed in `ArchitectureTests`. V17 grants the runtime role `DELETE`, and its policies allow it only to that session.
  - **Default, 30 days.** A key only has to outlive every retry of its request:
    - the web client makes one key per open form and resends it within seconds or minutes;
    - a form left open over a long weekend is a few days old.

    30 days is far beyond either, and it leaves a month to tie a replay to its resource when investigating.
  - **Floor, 7 days.** Never shorter, whatever the configuration says. The application clamps a shorter setting, and the database refuses to delete a younger key: the floor is a policy, like the nonce purge's replay window.
  - **After the purge,** a request sent again with a purged key is a new request. That is what the retention means, and why it is far longer than any retry.

## Not done
- **Commercial registration is not made unique:** registries differ by governorate, and the owner has not confirmed a rule.
