---
name: zawed-release-gate
description: The gate every ZAWED (زوّد) change must pass before it is called done — branch check, typecheck, lint, unit tests, production build, Playwright E2E of the core fueling flows, UI audit, code review, and the git and deploy rules. Use it whenever you are about to say work is finished, close a build phase (B0–B8), prepare a commit or push, or someone asks whether a change is safe to merge or ship.
---

# ZAWED release gate

Work is not done until every step that applies has passed. Report what ran and what it printed.
If a step fails, show the failing output; if a step was skipped, say so and why.

## 1. Branch

```bash
git branch --show-current
```

It must be `master` (the only branch since 2026-11-20), or a short-lived local branch of a parallel
agent that will be merged into `master` and deleted. The Railway service `zood-web` is the old investor
demo and is never deployed.

## 2. Web (`apps/web`) — whenever anything under it changed

```bash
cd apps/web
npm run check     # typecheck, lint, unit tests (includes i18n parity and token contrast)
npm run build     # production build
npm run e2e       # Playwright against the production build on 127.0.0.1:5180
```

The five flows in `e2e/fueling.spec.ts` are the product's core and must stay green:
main fueling with the live Company update, cooldown, daily-limit decline, printer failure, offline capture and sync.

One known environmental failure on this machine: individual requests refused with
`net::ERR_NETWORK_ACCESS_DENIED` (`docs/architecture/known-issues.md`). Playwright retries twice.
A run that fails only with that error can be re-run; any other failure is real.

## 3. API (`apps/api`) — once it exists

```bash
cd apps/api
./mvnw verify     # unit, integration and module-boundary tests
```

A change to the money path (ledger, wallet, fueling engine, sync ingest) also needs its concurrency
and idempotency tests to pass, not only the unit tests.

## 4. Review

- **UI changed:** run `zawed-ui-audit` on the changed screens in Arabic and English, Light and Dark, at 390, 768 and 1440 px. For a deeper design pass use `impeccable audit`.
- **Code:** run the `code-review` skill on the diff and fix real bugs.
- **Money, security or tenant isolation changed:** send the diff to the `zawed-reviewer` agent as well.

## 5. Documents

A document that the change makes wrong is a bug. Run `zawed-docs-sync`: it maps each kind of
change to the documents, the memory notes, the code graph and the diagrams that must follow it.
At the end of a build phase it also writes `docs/summaries/<phase>.md` (what was done, how to run
and test it, decisions and assumptions, what is deferred, open questions).

## 6. Git and deploy

- Commit or push only when the user asks. Stage explicit paths, not `git add -A`.
- Commit and push on `master` (pushing is approved by the owner); never push other branches. Never run `railway up` against `zood-web` or change its source branch.
- Deploys to `zawed-dev` need the owner's go-ahead, come only after this whole gate, and run from a clean worktree of the commit (API first, then web).
