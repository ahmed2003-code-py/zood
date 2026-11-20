# Secrets and devices

## The secrets

| Variable (service `zawed-api`) | Protects | Rotating it |
|---|---|---|
| `ZAWED_JWT_SECRET` | User access tokens and POS device tokens (HMAC) | **Possible, and disruptive for a few minutes.** Every access token and device token becomes invalid. Users sign in again or refresh. Devices get a new token on their next signed request, because their private keys are unchanged. Set the new value (at least 32 random bytes) and redeploy. |
| `ZAWED_DATA_KEY` (with `ZAWED_DATA_KEY_ID`, `ZAWED_DATA_KEYS_PREVIOUS`) | Data encrypted at rest: MFA (TOTP) secrets, pending enrolment secrets, bank account numbers, tag UIDs (AES-256-GCM, `DataEncryptor`) | **Possible, with no downtime: three deploys and a wait.** Follow [Rotating the data key](#rotating-the-data-key) below, step by step. Never just replace the value: everything encrypted with the old key would become unreadable. |
| `ZAWED_LOOKUP_KEY` | The keyed hashes that find a tag by its UID, device activation codes, receipt links, the devices' offline tag digest keys | **Do not change it.** Unset until the first data-key rotation, when it is set to the original `ZAWED_DATA_KEY` (step 1 below) and then kept for good. A new value would make every tag lookup miss. Rotating it is not built (ADR 0027, an owner decision). |
| `ZAWED_BOOTSTRAP_TOKEN` | The one-time first Super Admin setup | Should be **unset** once a Super Admin exists. Check it is unset. |
| `PGPASSWORD` (Railway Postgres) | The database | Rotate in Railway's Postgres service, then redeploy the API with the new value. |
| `SENTRY_DSN` | Where error reports go | Not a secret for reading data: it only allows *sending* events. Rotate in Sentry if it is abused. |

**Generating a value:** `openssl rand -base64 48`, or any 32+ byte CSPRNG output. A data key is **exactly** 32 bytes: `openssl rand -base64 32`. Never reuse a value across environments.

## The lookup key and the rotation window

`ZAWED_LOOKUP_KEY` is always the original `ZAWED_DATA_KEY`. Set it at step 1 and never change it. The API refuses to start if it is not key 1 while key 1 is in the ring, or if its fingerprint differs from the one in `data_key_state`. Rotating the data key does not fix a lookup-key leak: tag UIDs can be as short as 8 hex digits, so anyone with the lookup key and a database copy can reverse every `uid_lookup`, forge receipt links and derive device digest keys. A lookup-key leak needs the lookup-key rotation, which is an owner decision. Keep the rotation window short. During it, someone holding the current key and a runtime session can overwrite rows not yet re-encrypted. Watch for the snapshot's "tags left out" ERROR and for `BANK_ACCOUNT_REKEYED` audit rows that the job did not write (the job's rows have no actor).

## Rotating the data key

Why and how it works: [ADR 0027](../adr/0027-data-key-rotation.md). The API must be a build with schema 20 or later. Each step is one change of variables on `zawed-api` followed by a deploy; do not combine steps.

**Before you start:**
- Note the current key id: `ZAWED_DATA_KEY_ID`, or 1 when it is not set.
- The new id is that plus one. Ids only go up, and an id is never used again for another key.
- Generate the new key with `openssl rand -base64 32`.
- Keep the old and the new key in the owner's password manager until step 5.
- `ZAWED_DATA_KEYS_PREVIOUS` must be empty. If it is not, an earlier rotation is unfinished: finish it first.

1. **Stage the new key (deploy 1).** Every instance learns to read it before any instance writes with it, so the deploy overlap causes no errors.
   - `ZAWED_DATA_KEYS_PREVIOUS` = `<new id>:<new key>`.
   - At the first rotation only: `ZAWED_LOOKUP_KEY` = the current value of `ZAWED_DATA_KEY`. Later rotations leave it as it is.
   - Deploy. Nothing else changes. The log says `Data key rotation: every encrypted value is under the current key <old id>` about two minutes after start.
2. **Make it current (deploy 2).**
   - `ZAWED_DATA_KEY` = the new key.
   - `ZAWED_DATA_KEY_ID` = the new id.
   - `ZAWED_DATA_KEYS_PREVIOUS` = `<old id>:<old key>`.
   - Deploy. New values are written with the new key; old ones are still read with the old key.
   - If the API refuses to start, the message names the variable (for example a missing `ZAWED_LOOKUP_KEY`, or the same key under two ids). The previous deployment keeps serving.
3. **Wait for zero.**
   - The re-encryption job runs two minutes after start, then every 10 minutes, at most 10,000 values per column each run.
   - Each run logs `Data key rotation: current key <new id>; re-encrypted N, failed 0; M values not under the current key [...]`, with counts per column and key id, never a value.
   - Wait for `Data key rotation: every encrypted value is under the current key <new id>`.
   - **An `ERROR` line** (`failed` above 0, or a key this instance does not hold) means some value cannot be read. Stop here: do not remove the previous key. The line names the column and the key id; handle it as an incident ([incident.md](incident.md)).
4. **Retire the old key (deploy 3).** Remove `ZAWED_DATA_KEYS_PREVIOUS`, deploy, and check that the first run after start logs the "every encrypted value" line and no `ERROR`.
5. **Clean up.** Delete the old key from the password manager, unless it is `ZAWED_LOOKUP_KEY` (after the first rotation it is: keep it). Record the date and the new id in the incident or change log.

**Going back:**
- Until step 4, go back by configuration, not by build. Put back the step 1 values: the old key current, the new one as previous. Values already written with the new key stay readable, and the job leaves them alone.
- Once step 2 is deployed, never roll the build back to one older than schema 20: it cannot read the new key.

**If the key leaked:** rotate as above, and treat it as an incident. Rotation moves the data at rest to a new key, but the lookup hashes stay on the old key (`ZAWED_LOOKUP_KEY`). With that key and a copy of the database, someone could test guessed tag UIDs and forge receipt links. Tell the owner: rotating the lookup key is not built (ADR 0027).

## A POS device is lost or stolen

1. **Revoke it now** (Ops: Devices → the device → Revoke, which calls `POST /api/v1/ops/devices/{id}/revoke` with a reason).
   - Revocation is for good, and it takes effect at once. Every POS request checks that the device is `ACTIVE`, and that its operator is too (`PosCredentials`, `DeviceChannel`). A token the device already holds stops working on its next request.
2. **Sign out the agent** if their session was on the device, and reset their PIN.
3. **Check what it did while missing:**
   - its last sync time (Ops → sync monitor);
   - fuelings from it since then (Ops → fuelings, filtered by device).
   Offline captures it still holds cannot be synced once it is revoked, because the device can no longer authenticate. If those fuelings really happened (the device was taken *after* a shift), they are lost to the ledger. Reconcile them with the station from its own records.
4. **Replace it:** register a new device, give it an activation code, and activate it on the new terminal.
5. Write down the device id, the reason and the time in the incident log.

## A user's account is compromised

1. Suspend the user (Ops or Super Admin → Users). This ends all their sessions at once (`UserAdmin.setStatus` records `sessionsEnded` in the audit log).
2. Check the audit log for that user since the suspected time.
3. Create a fresh account, or reactivate this one after a password reset and MFA re-enrolment.
