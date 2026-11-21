# Test tags for the POS simulator

Use this when a tester or a demo needs to record fuelings on the web POS (`/pos`) against a real
API (`zawed-dev`, or a local API). The simulator has no RFID reader: the agent types the UID a
reader would return. No API gives out a full UID (tenants see the last four characters, the
terminal holds 32-bit digests), and that stays so. Testers use UIDs they chose themselves, issued
by Operations.

## 1. Pick UIDs nobody else uses

A UID is 8 to 32 hexadecimal characters. For tests, use the prefix `0E57` (reads "TEST"), your
initials as hex digits, and a counter, for example `0E57A1000001`, `0E57A1000002`. A UID already on
another vehicle is refused, so a clash shows up at once.

On `zawed-dev` a test UID is not a secret. On a real system a tag UID is the vehicle's key: never
write one into a ticket, a chat or a commit.

## 2. Operations issues the tag

In the Ops portal (Ops or Super Admin):

1. **Companies** → the test company → **Vehicles** → the test vehicle. Use an active vehicle of an
   active company with a funded wallet, unless the test is about a refusal.
2. **Issue tag** → type the UID from step 1 → **Issue**. The vehicle now shows "Tag ending …" with
   the last four characters.
3. For refusals, prepare one vehicle each: a tag reported **lost** (tag blocked), a **suspended**
   vehicle, a **frozen** company (wallet suspended), a vehicle with a small **daily limit**, and a
   vehicle whose fuel the station does not sell.

The same can be scripted: `POST /api/v1/ops/vehicles/{id}/tags` with `{"uid": "0E57A1000001"}`.

## 3. The terminal and the agent

1. Ops registers a POS device for the test station and shows its activation code once. The code is
   valid for a short time: when the station portal shows "Code expired, ask Operations", Ops issues
   a new one.
2. Ops creates the agent (worker ID and a temporary PIN). The agent chooses their own PIN at the
   first sign-in; the screen always offers "Sign out and end shift" if they cannot.
3. On `/pos`, activate with the device code and the activation code, then sign in.

## 4. Use the tag on the terminal

On **Read the tag**, type the UID and press **Continue**. The terminal checks it against its snapshot
(plate, company, fuel, wallet status word, limit left today, cooldown), asks for the volume and shows
the amount before recording.

**Simulator tools.** A development build (`npm run dev`) or a build made with
`VITE_POS_SIMULATOR_TOOLS=true` lists the UIDs typed on this browser under **Test tags** (marked
"Simulator only"), with the plate the terminal's snapshot matches, so the next read is one tap. They
stay in this browser's local storage (`zawed.pos.sim.tags`, at most 12) and are never sent anywhere.
Do not set the flag on a build that real agents use. The live e2e build sets it
(`playwright.live.config.ts`).

## How to tell it worked

- The receipt shows the server's reference (`F-…`) and its QR opens `/receipts/{id}?sig=…`.
- The fueling appears in the station portal under **Fuelings**, in the agent's shift under
  **Shifts**, and in the company portal of the vehicle's company.

## Clean up

Deactivate test tags you no longer need (vehicle → the tag → **Deactivate**; this is final). On a
shared environment, keep the list of UIDs in use in the QA notes outside the repository.
