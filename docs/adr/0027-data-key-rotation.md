# ADR 0027: Data-key rotation

**Status:** Accepted, 2026-11-20 · **Source:** B7 W9 (`docs/architecture/b7-plan.md`), found while writing the secrets runbook

## Context
`DataEncryptor` encrypts TOTP secrets, pending enrolment secrets, tag UIDs and bank account numbers with AES-256-GCM. Each value is laid out as `[key id: 1 byte][nonce: 12][ciphertext + tag]`, and the key id byte, followed by a per-row binding, is the associated data. The format had room for a second key, but the code knew only key 1. Changing `ZAWED_DATA_KEY` would therefore have made every value unreadable: MFA sign-in, the offline snapshot (it decrypts tag UIDs to build the device digests) and bank accounts would all break.

The same variable was also the master key of `LookupHasher`. That key makes the tag lookup hash (`rfid_tags.uid_lookup`, which the fueling path searches), the device activation code hashes, the receipt link signatures already sent to drivers, and the per-device tag digest keys that terminals hold offline. Moving that key silently would make every tag lookup miss, and some of those values cannot be rebuilt (activation codes are stored only as hashes, and sent links cannot be recalled).

## Decision
- **Key ring configuration** (`ZawedProperties.Security`):
  - `ZAWED_DATA_KEY` is the current key.
  - `ZAWED_DATA_KEY_ID` is its id (1 to 255, default 1).
  - `ZAWED_DATA_KEYS_PREVIOUS` lists the keys that only decrypt, as `id:base64` entries separated by commas.
  - A deployment that sets only `ZAWED_DATA_KEY` is key 1, exactly as before.
  - The API refuses to start with: an id outside 1 to 255; a malformed previous entry or a key that is not 32 bytes; an id listed twice; the current id also listed as previous; the same key bytes under two ids (a rotation that forgot to generate a key); and, when deployed, the committed development key anywhere in the ring or as the lookup key.
  - A previous id may be higher than the current one. This allows the staged first deploy below.
- **The lookup key is separate and does not rotate here.** `ZAWED_LOOKUP_KEY` is the `LookupHasher` master key. When unset it is `ZAWED_DATA_KEY`, which is allowed only while the key id is 1. From the first rotation on it is required, and it must differ from the current data key. In practice it is the original `ZAWED_DATA_KEY`, set once and never changed.
- **`DataEncryptor`** writes with the current key. It reads with the key that the value's first byte names (read as unsigned). A value under a key the instance does not hold fails with `Unknown key id N`; no other key is tried. The associated data is unchanged (the key id byte, then the binding). A test decrypts a value written by the pre-rotation code (`DataEncryptorTests`).
- **Re-encryption job** (`platform.crypto.DataKeyRotation`):
  - **Which values.** Each module declares its encrypted columns as `EncryptedColumn` beans, pointing at its own binding method: `SignIn::userBinding`, `SignIn::challengeBinding`, `Tags::associatedData` and `BankAccounts::binding`. The binding is therefore the same one the module itself writes and reads with. A schema test fails if a `bytea` column named `*_encrypted` is not declared.
  - **What it does.** For each column, the job takes values whose key id is lower than the current one, decrypts each with its own key, and encrypts it again with the current key under the same binding. It never writes a value under a lower id.
  - **Locking.** Batches of 100 run in short transactions. Rows are locked with `FOR UPDATE SKIP LOCKED` in primary-key order, so the job never waits for an application transaction. It holds no wallet lock, so it cannot deadlock with the fueling path's wallet-then-tag order.
  - **Each row.** Every update has its own savepoint and is a compare-and-set on the old value. A run handles at most 10,000 values per column.
  - **Schedule.** It runs every 10 minutes, first 2 minutes after start (`zawed.data-key-rotation.interval`, `.initial-delay`). It can be paused with `zawed.data-key-rotation.enabled=false`.
  - **Several instances.** Like `NonceRetention` (ADR 0024) it needs no lease (ADR 0011): two instances only share the work, and a value is never re-encrypted twice.
  - **Scope.** It is a platform-scope escalation listed in `ArchitectureTests`.
- **Database (V20):**
  - `data_key_id(bytea)` returns a value's key id.
  - `data_key_rekeyed(old, new)` is the shape of a re-encryption: the same length and a higher key id.
  - The tag UID guard (V14) and the bank account guard (V9) let exactly that change through, and only when nothing else in the row changes in the same update. Every other change of those columns is still refused for every role.
  - The runtime role may now write `station_bank_accounts.account_encrypted`, under that guard.
  - The job writes only the ciphertext, never `updated_at`.
- **Progress** is one log line per run. It gives counts only, never a value: how many values were re-encrypted, how many failed, and how many are still not under the current key, by column and key id. When everything is under the current key it says so once. Values under a key the instance does not hold, or values that do not verify, give an `ERROR` line, which becomes a Sentry event once `SENTRY_DSN` is set. There is no endpoint. A new read endpoint would add authorisation surface (and rows to the W2 sweep) for a figure that only the person doing the rotation needs, and that person reads the deploy logs.
- **Procedure:** see `docs/runbooks/secrets-and-devices.md`.
  1. Stage the new key as previous only, and pin `ZAWED_LOOKUP_KEY`.
  2. Make the new key current, with the old key as previous.
  3. Wait until the log reports zero.
  4. Remove the previous key.

  Each step is one deploy. Because of the staged step, every instance that serves traffic during an overlap can read both keys.

## Consequences
- A routine rotation causes no downtime and loses no data. A value is rewritten only after it has decrypted, under its row lock, with the old value as a precondition. An unreadable value is left exactly as it is and reported (`DataKeyRotationTests`).
- **Rotating after a leak does not rotate the lookup hashes.** The old key stays in the environment as `ZAWED_LOOKUP_KEY`. Someone who has that key and a copy of the database can still test guessed tag UIDs against `uid_lookup`, forge receipt links, and derive device tag digest keys. Rotating the lookup key would be a separate change:
  - re-hash the tag UIDs from their decrypted values;
  - re-issue pending activation codes;
  - accept that receipt links already sent stop working;
  - have every device refresh its snapshot.

  That last list has visible effects, so it is an owner decision.
- During a rotation, until the previous key is removed, a holder of the old key who can also write to the database could put a value under the old key into `users.mfa_secret_encrypted` or `mfa_challenges.pending_secret_encrypted`, which have no guard. Tags and bank accounts are protected by V20's "key id only goes up" rule. Retire the previous key as soon as the job reports zero.
- Rolling the build back to a version from before this ADR, once key 2 is current, fails loudly on key-2 values. During a rotation, roll back the configuration, not the build.
- There is no audit row per re-encrypted value: the plaintext and every other column are unchanged, and the run's log line records the counts.
- Key ids must never be reused for a different key. The runbook always takes a new, higher id.
