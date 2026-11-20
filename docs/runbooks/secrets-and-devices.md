# Secrets and devices

## The secrets

| Variable (service `zawed-api`) | Protects | Rotating it |
|---|---|---|
| `ZAWED_JWT_SECRET` | User access tokens and POS device tokens (HMAC) | **Possible, and disruptive for a few minutes.** Every access token and device token becomes invalid. Users sign in again or refresh. Devices get a new token on their next signed request, because their private keys are unchanged. Set the new value (at least 32 random bytes) and redeploy. |
| `ZAWED_DATA_KEY` | Data encrypted at rest: MFA (TOTP) secrets, bank account numbers, tag UIDs (AES-256-GCM, `DataEncryptor`) | **Not supported yet. Do not change it.** The ciphertext format carries a key id, ready for a second key, but the code knows only key 1. A new value would make every encrypted value unreadable: MFA sign-in, bank accounts and tag lookup would break. Rotation needs a change: read old and new keys, re-encrypt in the background, then retire the old key (an open B7 item). If the key leaked, treat it as an incident (see [incident.md](incident.md)) and build that change first. |
| `ZAWED_BOOTSTRAP_TOKEN` | The one-time first Super Admin setup | Should be **unset** once a Super Admin exists. Check it is unset. |
| `PGPASSWORD` (Railway Postgres) | The database | Rotate in Railway's Postgres service, then redeploy the API with the new value. |
| `SENTRY_DSN` | Where error reports go | Not a secret for reading data: it only allows *sending* events. Rotate in Sentry if it is abused. |

**Generating a value:** `openssl rand -base64 48`, or any 32+ byte CSPRNG output. Never reuse a value across environments.

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
