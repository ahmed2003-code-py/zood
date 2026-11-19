---
name: zawed-docs-sync
description: Keeps everything that describes ZAWED (زوّد) in step with the code — docs/architecture contracts, ADRs, phase summaries, the implementation plan, known issues, READMEs, CLAUDE.md, the auto-memory notes, the code graph (graphify-out) and the system diagrams (docs/architecture/diagrams). Use it EVERY time code changes in apps/api or apps/web — a new endpoint, migration, table, column, role, permission, setting, environment variable, screen, flow, limit, status or money rule — even when the request only says "add X" or "fix Y" and never mentions docs; when the Stop hook asks for it; at the end of a build phase; and whenever someone asks to update the docs, a diagram, the memory or "what changed".
---

# ZAWED docs sync — docs, memory, graph and diagrams

A change is not finished until the next person (or the next session) can find it and trust what
the documents say. A document the change made wrong is a bug. Do this in the same change as the
code, not as a follow-up.

## 1. Decide what the change touches

| The change… | Update |
|---|---|
| Adds or changes an endpoint, a request/response field, an error code | The phase contract in `docs/architecture/b*-*-contract.md`; regenerate `apps/api/openapi/openapi.json` and `npm run api:types` in `apps/web` if the API shape changed |
| Adds a migration, table, column, index, RLS policy or trigger | `docs/architecture/database-erd.md` (tables, keys, the multi-tenancy section) |
| Changes a decision that an ADR records (money, time, auth, idempotency, offline, deployment…) | That ADR in `docs/adr/` — amend it, or write a new numbered ADR and link it from `docs/adr/README.md`. Architecture decisions go through `zawed-deep` (model routing) |
| Changes money rules: ledger legs, wallets, payables, receivables, maker-checker | The contract, the ADR, and the ERD; say who approved the rule |
| Adds a role, permission, setting, feature flag or environment variable | The contract, `apps/api/README.md` (configuration), and the deploy notes in ADR 0014 |
| Adds or changes a screen, a route, a user flow in `apps/web` | `README.md` / `apps/web` conventions if a convention changed; `docs/demo/*.md` if the investor demo flow changed |
| Changes how the POS or driver app talks to the API | `docs/architecture/pos-and-driver-app.md` |
| Finds or fixes an environment problem | `docs/architecture/known-issues.md` |
| Closes or advances a build phase | `docs/summaries/<phase>.md` (what was done, how to run and test it, decisions, deferred, open questions) and the status in `docs/architecture/implementation-plan.md` |
| Changes code at all | The code graph (§3) |
| Changes something a diagram shows (a service, a module, a flow, the database) | The diagram (§4) |
| Changes how we work (a rule, a skill, a hook, the model routing) | `CLAUDE.md` and the affected skill |

Purely internal refactors with no behaviour change need only §3.

## 2. Memory — `C:\Users\ae11w\.claude\projects\c--Users-ae11w-OneDrive-Desktop-Zood\memory\`

The memory carries what the repository does not: state, owner decisions, open questions, gotchas.
Update it when the change moves any of these:

- **Project state** (`zawed-production-plan.md`): phase done, branch and head commit, what is
  deployed where, what is pending (reviews, owner decisions). Use absolute dates.
- **Owner decisions and preferences** (a `feedback` note): what the owner decided or corrected,
  with **Why** and **How to apply**.
- **Gotchas** that cost time (toolchain, Windows, Railway, test runner quirks).

Edit the existing note rather than adding a duplicate; keep `MEMORY.md` to one line per note.
Do not copy into memory what the code, git history or docs already record.

## 3. Code graph

The git hooks and the Stop hook refresh it automatically. If you need it current now (you are
about to rely on it, or you renamed or moved files):

```bash
GRAPHIFY_BACKEND=none "C:/Users/ae11w/AppData/Roaming/Python/Python312/Scripts/graphify.exe" update .
```

See `zawed-code-map` for how to read it.

## 4. Diagrams — `docs/architecture/diagrams/`

When the change alters what a diagram shows (a new module, a new outside service, a new step in
the fueling or sync flow, a new table), update its `.archify.json` after the code is committed:
re-pin `meta.repository.revision`, fix the cited lines, run `finalize` (command in
`zawed-code-map` §5), commit it with the next commit. A new area worth a picture gets a new
diagram and a row in the folder's README.

## 5. Check and report

- Re-read each edited document once against the code: names, numbers, paths and statuses match.
- Arabic documents use Egyptian Arabic and keep code identifiers in English.
- Report in one or two lines what was updated ("docs: b4 contract + ERD; memory: production plan;
  graph refreshed") or why nothing needed it. Never claim a document is current without opening it.
