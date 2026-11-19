# ZAWED (زوّد)

B2B fleet fuel management platform for Egypt. `apps/web` is the React prototype behind the investor
demo and runs on mock data. The production build (Spring Boot API in `apps/api`, the same web app,
a Kotlin POS later) is planned in `docs/architecture/implementation-plan.md`.

## Rules that always apply

- **Branches:** work on `develop` or `feature/*`. Never commit to, merge into or push `master`, and never deploy to the Railway service `zood-web`: they are the frozen investor demo. Commit or push only when the user asks.
- **Phases:** work goes in controlled phases. State the objective, the files and the assumptions before starting a phase, and wait for approval unless told to work autonomously.
- **Web conventions** are enforced by lint and tests (see `README.md`): no literal UI text, both languages in the same change, logical direction utilities, semantic colour tokens only, no `dark:` variant, money in integer piastres, volume in integer millilitres.
- **Sources of truth, in order:** approved decisions (`docs/demo/overnight-decisions.md`) → roadmap and architecture documents → PRD v6 → the prototype.

## Skill pipeline

Every prompt and every step goes through the skills that fit it, in this order. A hook
(`.claude/hooks/prompt-pipeline.mjs`) repeats the short version on every prompt. Skip a stage only
when it clearly does not apply (a question, a one-line answer); never skip stage 4 after a code change.

| Stage | When | Skills |
|---|---|---|
| 1. Understand | Before changing or explaining code across files | `zawed-code-map` (graph first, then read only what it points at) |
| 2. Approach | New feature or behaviour | `superpowers:brainstorming`, then `superpowers:writing-plans` for multi-step work |
| | Bug, failing test, unexpected behaviour | `superpowers:systematic-debugging` |
| | Writing the code | `superpowers:test-driven-development` (test first where the code is testable) |
| 3. Build | Anything visual in `apps/web` | `zawed-design-system` first, then `vercel-react-best-practices` (React parts only: we use Vite, not Next.js), `impeccable` / `frontend-design` for design passes |
| | TypeScript types | `typescript-advanced-types` |
| | Web unit tests | `vitest` |
| | E2E tests | `playwright-best-practices`, `webapp-testing` |
| | Spring Boot code | `java-springboot` |
| | Java tests | `java-junit` |
| | SQL, migrations, indexes, RLS | `supabase-postgres-best-practices` (Postgres rules only: ignore Supabase-specific parts such as `auth.uid()`; our RLS reads `app.scope_type` / `app.scope_id` / `app.user_id`, see the multi-tenancy section of `database-erd.md` and `V3__runtime_role_and_rls.sql`) |
| | Money path, RLS, auth, devices, ADRs | hand to `zawed-deep` (model routing below) |
| 4. Close | After any code change, before "done" | `zawed-docs-sync` (docs, memory, graph, diagrams), `superpowers:verification-before-completion`, then `zawed-release-gate` |
| | Reviewing | `zawed-ui-audit` for UI, `code-review` for code, `zawed-reviewer` for money or security |

Precedence when skills disagree: this file → `docs/demo/overnight-decisions.md` and the ADRs →
the `zawed-*` skills → Superpowers and community skills. In particular:
- **Git:** Superpowers' worktree and branch-finishing skills never commit, merge or push without
  the user's request, and never touch `master` (rules above).
- **Subagents:** Superpowers' subagent-driven development follows the model routing table below.
- **Phases:** brainstorming and plans are how a phase's objective, files and assumptions are stated
  for approval.

Automatic upkeep: the code graph (`graphify-out/`) rebuilds after every commit, checkout and merge
(git hooks) and after any turn that changed code (Stop hook). A second Stop hook asks once per set of
changed files for `zawed-docs-sync` when code changed without documents.

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
