# ZAWED handover: where we are and how we work

**Written:** 2026-11-20, at the end of a long session on the owner's first laptop. The owner continues on another machine, with another Claude account, from a fresh clone.
**Read this first, then `CLAUDE.md`, then `docs/architecture/b7-plan.md`.**

> **الخلاصة بالمصري**
> - **المراحل:** خلصنا من B0 لحد B6، وشغل الـ hardening (V12–V16) متعمل له merge في `develop` وشغال على `zawed-dev`.
> - **دلوقتي في B7:** الـ branch الأساسي `feature/b7-hardening`. فيه ٧ branches تانية لكل جزء، منهم ٤ خلصانين ومستنيين يتدمجوا بترتيب معيّن، و٢ لسه شغل ماخلصش (WIP).
> - **بيئة التجربة `zawed-dev`:** الـ authenticator مقفول فيها، وفيها حسابات تيست لكل الأدوار.
> - **المرحلة الجاية U1:** تحسين الواجهة وصفحة تعريف (landing page) أقوى من PetroApp وOctane.
> - **B8 (الـ POS):** مع الـ mobile developer، مش معانا.
> - **الرد على صاحب المشروع:** دايماً بالعربي المصري.

---

## 1. The project in one paragraph

ZAWED (زوّد) is a B2B fleet-fuel platform for Egypt.
- **How it works:** companies prepay a wallet. Their vehicles fuel at partner stations, where a POS reads an RFID tag and checks limits and balance. Every litre is posted in a double-entry ledger, and stations are paid out from their payables.
- **Repository:**
  - `apps/api`: Spring Boot 4, Java 25, PostgreSQL 18, Flyway, Spring Modulith, row-level security.
  - `apps/web`: React, Vite and TypeScript, with the company, station and Ops portals plus a POS simulator. The investor demo in it runs on mock data.
  - `docs/`: the plan, the ADRs, the contracts, the runbooks and the security pack.
- **The native Kotlin POS (B8)** is built by a separate mobile developer. We keep the POS contract stable.

## 2. Repository state (all pushed to GitHub)

| Branch | State | What it holds |
|---|---|---|
| `master` | **Frozen.** The investor demo. Never commit, merge or push it. | |
| `develop` | `f3b5827` | B0–B6, plus the production hardening (V12–V16): maker-checker top-ups, idempotency keys, fleet identity, bulk vehicle import, fueling indexes. This is what was deployed before B7. |
| `feature/b7-hardening` | **Main B7 branch.** Deployed to `zawed-dev`. | The skill pipeline and agents setup, plus the B7 work done in the main session. **Fleet import:** every row failure is recorded and a job can no longer stall; counters are recomputed from the rows; `INVALID_FLEET_CODE`; XLSX parts are scanned whole for DOCTYPE/ENTITY. **MFA switch:** `ZAWED_MFA_REQUIRED` (default true; production refuses false). **Sentry:** wired in and switched off. Plus the runbooks, the security review pack, the B7 plan and the U1 phase in the plan. |
| `feature/b7-w1-money` | **Done, reviewed twice, ready to merge.** | V17: top-up guards, an idempotency-key purge through a `SECURITY DEFINER` function, an `Idempotency-Key` required on wallet adjustments, the company offline alert raised only once, the withdrawal freeze, and a strict replay check on recorded transfers. 325 tests green. |
| `feature/b7-w2-idor` | **Done; its review was interrupted.** | `IdorSweepTests`: 171 endpoints and 422 cross-tenant attack cases. One real hole was **fixed**: a company could link another operator's station to a ticket. 316 tests green. |
| `feature/b7-w5-notifications` | **API done, review found 2 Medium issues; the fix is WIP.** | V19 and the `notifications` module (11 event types, RLS, 4 endpoints). The last commit `b999005` is a **red WIP test** for finding M1. |
| `feature/b7-w9-datakey` | **Done; its review was interrupted.** | Data-key rotation: a keyring, a separate `ZAWED_LOOKUP_KEY`, the `DataKeyRotation` job, V20 and ADR 0027. 337 tests green. |
| `feature/b7-w10-login` | **WIP, not tested.** | One sign-in page for every role (the admin door removed). The last commit `d8ce8d4` is unfinished work. |
| `feature/zawed-production-hardening`, `feature/perf-evidence` | Already merged into `develop`. | |

### Merge order and migration numbers

Flyway refuses a version lower than one already applied, and out-of-order migrations are off. So:
- **V17** (W1) → **V19** (W5) → **V20** (W9). **V18 is never used.** W4, the session and MFA-challenge purge, takes **V21**.
- Merge into `feature/b7-hardening` in this order: **W1 → W2 → W5 (after its fixes) → W9 → W10 (after it is finished)**. Expect small conflicts in `TopUps.java` and `WalletAdjustments.java` between W1 and W5.
- The ADR numbers clash: W9 used `0027-data-key-rotation.md`, and the plan had reserved 0027 for retention. **W4 takes ADR 0028.**
- After each merge, run the full API suite **on a quiet machine** (see §7) and the web `npm run check`.

### Patches and text not yet applied

- `docs/handover/pending-patches/w9-docs-for-b7-hardening.patch` updates the runbook, the review pack and the plan for W9. Apply it when W9 is merged: `git apply`.
- **W2's doc text** (apply when W2 is merged):
  - review pack §6: delete the row "No automated IDOR sweep yet…" and add "A top-up request answers `409 BANK_REFERENCE_IN_USE` for a reference another company claimed | Owner decision pending (`known-issues.md`)";
  - review pack §4, Authorisation row: append ", and an IDOR sweep of every endpoint × scoped role × the other tenant's ids | `fueling/IdorSweepTests.java`";
  - b7-plan row W2: mark it done, with `3636349` and `bf64128`.

## 3. What is open, by work stream (B7)

| # | Stream | Next action |
|---|---|---|
| W1 | Money review fixes | **Merge.** |
| W2 | IDOR sweep | Rerun the `zawed-reviewer` pass, which a usage limit interrupted. The brief is in §6. Then merge. |
| W3 | Sentry | Postponed by the owner. It stays off until DSNs are given. |
| W4 | Retention | Purge expired and revoked `sessions` and expired `mfa_challenges` through a `SECURITY DEFINER` function, on the reviewed pattern of `purge_idempotency_keys` and `purge_device_nonces`. **Never purge `agent_sessions`**: shifts are built from it. Uses V21 and ADR 0028. Goes to `zawed-deep`. |
| W5 | Notifications | Fix the review findings first. **Required:**<br>**M1:** a notification failure must not stop a fueling from being recorded. Use a savepoint for notifications caused by fuelings; the WIP test `NotificationFailureTests` is red.<br>**M2:** Ops' unread count scans everything. Add a per-user "read up to" mark.<br>**Low:**<br>**L1:** a user-less platform session may only insert `WALLET_LOW_BALANCE` and `HARD_ALERT_RAISED`, and the writer regex in `ArchitectureTests` must be widened.<br>**L2:** allow a fixed set of params keys per type.<br>**L4:** the cursor query.<br>Then rerun the review, merge, and have `zawed-bulk` build the **web bell and list**. |
| W6 | Runbooks | Done. A restore onto a **new server** still has to be rehearsed on the pilot host. |
| W7 | Test tenant and k6 | The test tenant **exists on `zawed-dev`** (§5). Next is the k6 run (`tools/load/`). |
| W8 | Security review pack | Done (`docs/security/review-pack.md`). |
| W9 | Data-key rotation | Rerun the interrupted `zawed-reviewer` pass (brief in §6), merge, then apply the pending patch. |
| W10 | One sign-in page | Finish the WIP: the web route `/admin/login` must redirect to `/login`, ADR 0008 needs an amendment, and the tests must pass. Then a review, then merge. |

### Owner decisions still pending (ask him; don't guess)

1. **Offline alert window:** the company offline-exposure alert uses a 24-hour window of server time. Should it be judged by capture time instead? (ADR 0025)
2. **Adjustment replay status:** a wallet-adjustment replay answers 201, but ADR 0022 says 200. Change it?
3. **Super Admin and approvals:** the Super Admin holds TOPUP_APPROVE but gets no approval notifications. Should it?
4. **Rejected Ops-recorded transfer:** when a transfer recorded by Ops is rejected, the company is told nothing. Should it be?
5. **Top-up reference conflict:** a top-up reference already claimed by another company answers 409. Keep that, or let the second claim stand as pending and settle it at verification (a money-path migration)? (`known-issues.md`)
6. **Lookup-key rotation:** build rotation for `ZAWED_LOOKUP_KEY` after a leak? It re-hashes tags, re-issues activation codes, breaks receipt links that were already sent, and refreshes device snapshots.
7. **Older decisions:**
   - the offline receivable ceiling;
   - whether a commercial registration must be unique;
   - whether the plate category is part of identity;
   - the payout threshold;
   - who pays for VIP fuel.

## 4. The next phase: U1 (UI revamp) and the landing page

The owner tested `zawed-dev` and found **many UI bugs**; the screens are not comfortable yet. U1 is in `docs/architecture/implementation-plan.md` §7. Its steps:
1. Audit every live screen with `zawed-ui-audit` and `impeccable`, in both languages and both themes, at 390, 768 and 1440 px, and add the owner's own bug list.
2. Agree the visual direction with him.
3. Fix the shared components and the shell first, then the screens in priority order.
4. Set up screenshot baselines.
5. Build the **public landing page**.

### Landing page: what the owner wants (2026-11-20)

- **The bar:** it must be **stronger and more polished than the two main competitors, PetroApp (https://petroapp.com/egypt/) and Octane (https://www.octane-tech.io/ar)**. Match their structure and level, but **never copy their text, images or layout**: they are competitors, and copying is a legal and credibility risk.
- **Motion:** an entrance animation and a moving gradient background (he liked PetroApp's moving blue background), plus scroll reveals, all modern and dynamic. Respect `prefers-reduced-motion`.
- **Identity:** the visitor should feel this is an **Egyptian company**. Use Egyptian Arabic copy, local references (EGP, the Egyptian e-invoice rules, a team in Egypt, support in Egyptian Arabic) and Egyptian context.
- **Languages and themes:** Arabic RTL and English LTR, Light and Dark, phone and desktop, built on the design system (`zawed-design-system`; tokens only).
- **Numbers:** **features instead of numbers.** There is no real data before the pilot, so never invent "600 stations" or "35% savings". Use claims the product can prove: every litre recorded at the moment it happens, two-person approval on money, fueling that works offline, statements to the piastre.
- **Pricing:** **placeholder packages** like the competitors (tiers by fleet size, monthly or yearly). The owner will replace the numbers later, so mark them clearly as placeholders in the code.
- **Contact:** a **"Request a demo" form**. Its fields are company name, contact person, phone, e-mail and fleet size. A request becomes a lead in the Ops portal, so it needs a public endpoint with a rate limit and spam protection, plus a leads table visible to platform roles only.
- **Temporary contact details, from the owner:** WhatsApp **01002589923** and e-mail **zawed-fual@gmail.com**. "fual" may be a typo for "fuel", so confirm with the owner before publishing.
- **Proposed sections:**
  1. The hero: a POS fueling animated live (the tag is read, the wallet is debited, the receipt comes out).
  2. The problem: fuel cards, cash, paper invoices, fraud.
  3. How it works, in 3 steps: the company tops up, the driver fuels with a tag, every litre is recorded.
  4. Separate sections for fleets and for stations. PetroApp has nothing for stations.
  5. Trust and safety.
  6. "Made in Egypt".
  7. Pricing (placeholders).
  8. FAQ.
  9. The demo form.
- **The demo stays at `/demo`.** The landing page becomes `/`.
- **Competitor notes, from reading PetroApp's page:**
  - it is mostly in English and text-led, with a dashboard screenshot;
  - it lists 7 features, 4 stat cards and 6 price tiers by fleet size (monthly or yearly, plus an NFC chip price and an installation fee);
  - a contact form and WhatsApp;
  - no client logos or testimonials, and nothing for stations;
  - Octane's page was not read yet, because a usage limit hit. Read it first.

**Also decided:**
- **One sign-in page for everyone.** That is W10 above.
- **MFA is off on `zawed-dev` while the owner tests.** Turn it back on before any real use by removing `ZAWED_MFA_REQUIRED`.

## 5. Environments and access

- **`zawed-dev`** is Railway project `5e712b7f-0503-4cb6-9024-349c62f6e57c`, environment `production`, with the services `zawed-api`, `zawed-web` and `Postgres`.
  - API: https://zawed-api-production.up.railway.app/api/v1 (`/meta` shows `schemaVersion`; it is **16** now).
  - Web: https://zawed-web-production.up.railway.app
  - The database has no public URL. Reach it with `railway ssh --service Postgres -- psql -U postgres -d railway -f - < file.sql`, run from `apps/api`, which must be linked with `railway link`. Wrap checks in `BEGIN READ ONLY`.
  - `ZAWED_MFA_REQUIRED=false` is set on `zawed-api`, for testing.
- **Investor demo:** Railway project "zood" / service `zood-web`. **Frozen. Never deploy to it.**
- **Test data on `zawed-dev`** (seeded 2026-11-20):
  - 3 companies; the main one is "Nile Transport & Services".
  - 50 vehicles with tags.
  - 1 operator, "Elite Station Management", with 2 stations, 6 POS devices pending activation and 3 agents.
  - Prices.
  - Nile's wallet holds 500,000 EGP from one top-up approved through the two-person flow.
- **Test accounts:** `test.ops@zawed.test`, `test.ops.manager@zawed.test`, `test.fleet.manager@zawed.test`, `test.accountant@zawed.test` and `test.station.manager@zawed.test`, plus the owner's Super Admin. **Passwords are not in this repository.** The owner has them in his handover prompt and in `accounts.md` on the first laptop.
  - Until W10 is merged, the Super Admin signs in at `/admin/login`; everyone else uses `/login`.
- **Device activation codes and agent PINs** for `zawed-dev` are kept only on the first laptop (`C:\Users\ae11w\zawed-test-accounts\`). On a new machine, create new devices and agents from the Ops portal instead.

## 6. How we work: skills, agents and models

### The pipeline (`CLAUDE.md` → "Skill pipeline")

Every task goes through four stages:
1. **Understand:** `zawed-code-map` (graph first).
2. **Approach:**
   - a new feature → `superpowers:brainstorming`, then `writing-plans`;
   - a bug → `systematic-debugging`;
   - writing code → `test-driven-development`.
3. **Build** with the domain skills:
   - `zawed-design-system` (plus `vercel-react-best-practices` and `typescript-advanced-types`) for the web;
   - `vitest`, `playwright-best-practices` and `java-junit` for tests;
   - `java-springboot`;
   - `supabase-postgres-best-practices`, Postgres rules only.
4. **Close:** `zawed-docs-sync`, then `superpowers:verification-before-completion`, then `zawed-release-gate`.

**Hooks** (`.claude/settings.json`):
- a reminder on every prompt;
- a Stop hook that asks for docs-sync once for each set of changed files;
- an async graph refresh.

### Model routing (`CLAUDE.md` → "Model routing")

- **Fable is no longer available.** `zawed-deep` and `zawed-reviewer` run on **Opus**.
- **`zawed-deep`** handles the money path, RLS, authentication, devices and ADRs.
- **`zawed-reviewer`** reviews money and security work, read-only.
- **`zawed-bulk`** (Sonnet) handles repetitive work, for example screens built from an existing pattern.
- **The main session (Opus)** does everything else.
- **Always tell the owner in one line which model did which part.**

### The agent technique that worked

1. **One work stream, one branch, one worktree.**

   ```bash
   git worktree add -b feature/b7-wN-name <scratch>/b7-name-wt <base>
   ```

   The brief tells the agent to work **only** in that worktree and never touch the main checkout. It also forbids push, merge and deploy, says which migration number to use, and gives the commit trailer.
2. **A brief must stand alone:**
   - the goal and why;
   - the files to read first;
   - the constraints (lock order, RLS pattern, error codes);
   - the tests required, including concurrency tests for concurrency claims;
   - the docs to update;
   - what to send back, with a word limit.

   The agent starts with zero context.
3. **Every money or security branch gets an independent `zawed-reviewer` pass.** Give the reviewer the claims to *verify*, not to trust. Send the findings back to the **same author agent** with `SendMessage`, so it keeps its context. Then run a short **re-review** of just the fix commits.
4. **Check before relying:** read the diff, and run the suite yourself before merging.
5. **Usage limits and SSL drops kill agents mid-task.** Their work is saved in the worktree. Resume them with `SendMessage` ("resume where you stopped: a usage limit cut you off, not a code problem") rather than starting new ones. When the owner switches accounts, resuming works again.
6. **Before a hand-off or an interruption**, commit unfinished agent work as `WIP(...)` with a clear message, and push the branch.

### Briefs to rerun the interrupted reviews

- **W2 review:**
  - the ticket fix at `Tickets.java:~122`: is it complete, and is there the same company exemption elsewhere?
  - can the sweep miss endpoints or identifiers? Check `@RequestParam` maps, `@ModelAttribute`, String ids (short references, device codes, worker ids, bank references), multipart and headers; B's ids in **every** id position; the exact comparison with a nonexistent id; the leak markers; the write digest covering every B table;
  - are the two accepted oracles (`CLIENT_TXN_CONFLICT`, `BANK_REFERENCE_IN_USE`) acceptable?
- **W9 review:**
  - crypto: nonces, the key id in the AAD, an unknown id failing loudly, no fallback for encryption, the AAD per column against the original call sites;
  - the lookup-key separation is backward compatible;
  - whether V20's guard relaxation lets a `zawed_app` session swap a tag UID or a bank account by claiming a higher key id;
  - the job's resumability and deadlock freedom, failures counted, no values in the logs;
  - how serious the two owner decisions are (lookup-key rotation; MFA secrets planted during a rotation window).

## 7. Machine setup and gotchas (Windows)

- **Tools:**
  - JDK 25 (Temurin) and Node 22.
  - The Railway CLI, logged in with `railway login`, then `railway link` inside `apps/api` to `zawed-dev`.
  - Python 3.12, then `python -m pip install --user "graphifyy[sql]"` for the code graph, then build it with `graphify extract . --code-only` and `graphify cluster-only . --backend=none`, and add the git hooks with `graphify hook install`.
  - Archify: `git clone https://github.com/tt-a1i/archify <home>/.darsly-tools/archify`, for diagrams.
  - Fix the graphify and archify paths in `zawed-code-map` if they differ on the new machine.
- **Superpowers** is enabled in `.claude/settings.json` from the official marketplace. Approve the plugin when Claude Code asks.
- **Never run two API suites at once.** The embedded PostgreSQL then fails with `BindException`, `Permission denied: connect`, `The connection attempt failed`, and then "ApplicationContext failure threshold exceeded", a cascade that looks like 200+ errors. They are **environmental**. Run the gate suite on a quiet machine. Agents can isolate runs with `JAVA_TOOL_OPTIONS=-Djava.io.tmpdir=<own dir>`. A clean run today is **322** tests on `feature/b7-hardening`.
- **Run vitest and `npm run check` from PowerShell** after `Set-Location 'C:\...'` with an **upper-case C:**. A lowercase `c:` working directory makes vitest fail with "failed to find the runner".
- **Deploying:**
  - always from a clean worktree of the commit (`git worktree add --detach <path> <sha>`);
  - run `railway up --ci --project <id> --environment production --service zawed-api|zawed-web` **from `apps/api` or `apps/web`**, never from the root;
  - deploy the API first, then the web.
- **Railway IaC:** `.railway/railway.ts` declares the variables with `preserve()`. Add every new variable there too, so `railway config apply` does not drop it.
- **Bash heredocs with backslashes or apostrophes** can get mangled in this harness. Write scripts with the Write tool.
- **Commits and pushes happen only when the owner explicitly says so.** An inferred commit was refused by the auto-mode classifier.

## 8. Memory

The first laptop's Claude memory is copied to `docs/handover/memory/`, with passwords removed. On the new machine, import those notes into Claude's memory. The two that matter most are `reply-in-arabic.md` (always answer the owner in Egyptian Arabic) and `zawed-git-policy.md` (master and `zood-web` are frozen).

## 9. Exact next steps for the new session

1. Clone the repository, `git fetch --all`, and check out `feature/b7-hardening`. Read this file, `CLAUDE.md` and `docs/architecture/b7-plan.md`.
2. Set up the machine (§7). Import the memory notes (§8).
3. Ask the owner for **explicit permission** before any commit, merge, push or deploy.
4. **Merge W1** into `feature/b7-hardening`, run the full suite on a quiet machine, and push.
5. **W5:** have `zawed-deep` fix M1, M2 and the Lows. Re-review, merge, push. Then `zawed-bulk` builds the web bell and list.
6. **W2 and W9:** rerun their reviews, fix any findings, merge in order (W2, then W9), and apply the pending patch and the text.
7. **W10:** finish, review, merge.
8. **W4:** retention (V21, ADR 0028).
9. Run the full gate and deploy to `zawed-dev` (API first). Check `schemaVersion` = 20.
10. Start **U1**:
    - read Octane's page;
    - write the landing-page design spec and show it to the owner;
    - build the landing page and the demo-request form;
    - then audit and fix the portals with the owner's bug list.
