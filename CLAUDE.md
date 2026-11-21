# ZAWED (زوّد)

B2B fleet fuel management platform for Egypt. `apps/web` is the React prototype behind the investor
demo and runs on mock data. The production build (Spring Boot API in `apps/api`, the same web app,
a Kotlin POS later) is planned in `docs/architecture/implementation-plan.md`.

**New session or new machine? Start with `docs/handover/HANDOVER.md`.** It holds the current state of every branch, the open work, the owner's decisions and how we work with agents.

## Rules that always apply

- **Branches (owner, 2026-11-20):** there is **one branch, `master`**. All work is committed on it and pushed to it; do not create branches on GitHub. Parallel agents may use local worktrees on short-lived local branches, which are merged into `master` and deleted, never pushed. Run the gate (full API suite alone, web `npm run check`) before pushing. The old investor demo stays at `/demo` and as tag `investor-demo-v1`; never deploy to the Railway service `zood-web`. Deploys to `zawed-dev` need the owner's go-ahead.
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
| | User-facing prose (landing page and UI copy in both languages, docs, PR text, messages to the owner) | `humanizer` (owner, 2026-11-20): remove AI writing patterns; never change facts, numbers, code, keys or identifiers |
| | Reviewing | `zawed-ui-audit` for UI, `code-review` for code, `zawed-reviewer` for money or security |

Precedence when skills disagree: this file → `docs/demo/overnight-decisions.md` and the ADRs →
the `zawed-*` skills → Superpowers and community skills. In particular:
- **Git:** Superpowers' worktree and branch-finishing skills follow the one-branch rule above: work
  lands on `master`; no branches are pushed.
- **Subagents:** Superpowers' subagent-driven development follows the model routing table below.
- **Phases:** brainstorming and plans are how a phase's objective, files and assumptions are stated
  for approval.

Automatic upkeep: the code graph (`graphify-out/`) rebuilds after every commit, checkout and merge
(git hooks) and after any turn that changed code (Stop hook). A second Stop hook asks once per set of
changed files for `zawed-docs-sync` when code changed without documents.

## Model routing

The owner asked for this on 2026-11-12: use the model that fits each task, without being told each
time. Delegating under these rules counts as the user asking for a subagent.

Fable is no longer available (owner, 2026-11-19): `zawed-deep` and `zawed-reviewer` now run on
Opus. They stay separate agents so that money and security work and its review still get a
dedicated brief and a fresh pair of eyes.

The main session runs on Opus (`.claude/settings.json`) and does ordinary work itself. It hands a
task to another model only when the task matches a row below. A subagent ignores this section and
does the task it was given.

| Task | Goes to | Model |
|---|---|---|
| Money path: ledger postings, wallet locking, idempotency, reconciliation, the fueling engine, offline sync ingest | `zawed-deep` | Opus |
| Tenant isolation (RLS), authentication, sessions, device security | `zawed-deep` | Opus |
| Architecture decisions and ADRs | `zawed-deep` | Opus |
| Review of finished money or security code, and of each build phase before it closes | `zawed-reviewer` | Opus |
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
