# ZAWED (زوّد)

B2B fleet fuel management platform for Egypt. `apps/web` is the React prototype behind the investor
demo and runs on mock data. The production build (Spring Boot API in `apps/api`, the same web app,
a Kotlin POS later) is planned in `docs/architecture/implementation-plan.md`.

## Rules that always apply

- **Branches:** work on `develop` or `feature/*`. Never commit to, merge into or push `master`, and never deploy to the Railway service `zood-web`: they are the frozen investor demo. Commit or push only when the user asks.
- **Phases:** work goes in controlled phases. State the objective, the files and the assumptions before starting a phase, and wait for approval unless told to work autonomously.
- **Web conventions** are enforced by lint and tests (see `README.md`): no literal UI text, both languages in the same change, logical direction utilities, semantic colour tokens only, no `dark:` variant, money in integer piastres, volume in integer millilitres.
- **Sources of truth, in order:** approved decisions (`docs/demo/overnight-decisions.md`) → roadmap and architecture documents → PRD v6 → the prototype.

## Skills

- Before any UI change in `apps/web`: `zawed-design-system`.
- When reviewing a screen or UI diff: `zawed-ui-audit`.
- Before calling work done, closing a phase, or preparing a commit: `zawed-release-gate`.

## Model routing

The owner asked for this on 2026-11-12: use the model that fits each task, without being told each
time. Delegating under these rules counts as the user asking for a subagent.

The main session runs on Opus (`.claude/settings.json`) and does ordinary work itself. It hands a
task to another model only when the task matches a row below. A subagent ignores this section and
does the task it was given.

| Task | Goes to | Model |
|---|---|---|
| Money path: ledger postings, wallet locking, idempotency, reconciliation, the fueling engine, offline sync ingest | `zawed-deep` | Fable |
| Tenant isolation (RLS), authentication, sessions, device security | `zawed-deep` | Fable |
| Architecture decisions and ADRs | `zawed-deep` | Fable |
| Review of finished money or security code, and of each build phase before it closes | `zawed-reviewer` | Fable |
| Everything else: features, endpoints, screens, tests, wiring, debugging | the main session | Opus |
| Repetitive, fully specified work across many files: screens or endpoints from an existing pattern, translation keys, renames, boilerplate tests | `zawed-bulk` | Sonnet |
| Finding files or usages across the repository | `Explore` | built-in |

How to delegate:

- One self-contained task per agent. A subagent starts with no memory of this conversation, so the brief carries the goal, the files, the constraints, what "done" means and what to send back.
- Keep in the main session: small edits, anything that needs back-and-forth with the user, and work that depends on context only this conversation has. The hand-off would cost more than it saves.
- Split a mixed task: the hard core goes to `zawed-deep`, the surrounding wiring stays here.
- Check what comes back before relying on it: read the diff and run the tests.
- Tell the user in one line which model did which part.
- If the user has switched the main session to another model, that is their choice for the session: keep the review and bulk rows, and do the rest in the main session.
