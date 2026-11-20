---
name: zawed-skill-pipeline
description: "Owner wants every prompt and step routed through the matching skills, and docs + memory kept current continuously; what was installed on 2026-11-19 and which parts are local-only"
metadata:
  node_type: memory
  type: feedback
  originSessionId: a8008b01-f582-47d7-92c9-f8bbcc1da429
  modified: 2026-11-19T12:03:23.645Z
---

On 2026-11-19 the owner asked that every prompt and every step in ZAWED go through the fitting skills
("لازم تعدي علي الاسكيلز دي") and that docs and memory be updated as work happens, not afterwards.

Set up that day (uncommitted, on feature/zawed-production-hardening):
- The Superpowers plugin (official marketplace, project scope, v7.0.2). It has a SessionStart hook that makes skill checks mandatory.
- Community skills in `.claude/skills/`, each read before use; all markdown, no scripts: vercel-react-best-practices, typescript-advanced-types, vitest, playwright-best-practices, java-springboot, java-junit, supabase-postgres-best-practices. Versions are pinned in `skills-lock.json`; update with `npx skills update`.
- `zawed-code-map`, ported from Darsly's nagahny-code-map: Graphify for the code graph, Archify for diagrams, including tree diagrams. `zawed-docs-sync` keeps docs, memory, the graph and the diagrams in step.
- The "Skill pipeline" table in `CLAUDE.md`, plus project hooks in `.claude/settings.json`: UserPromptSubmit `prompt-pipeline.mjs`, Stop `docs-sync-check.mjs` (blocks once per set of changed files) and Stop async `graph-refresh.mjs`.
- First diagram: `docs/architecture/diagrams/modules.archify.json` (tree, pinned to 1a790f6).

Local-only, never committed (must be redone on another machine):
- `graphify-out/` and `.graphifyignore` (in `.git/info/exclude`).
- The Graphify git hooks (`graphify hook install`).
- The merge attribute in `.git/info/attributes`.
- The Archify tool at `C:\Users\ae11w\.darsly-tools\archify`.

**Why:** the owner wants the best result on every step and documents that are never stale.
**How to apply:** follow the CLAUDE.md pipeline. Run `zawed-docs-sync` before calling any code change done, and update [[zawed-production-plan]] when state moves. CLAUDE.md and the ADRs win over Superpowers: no commits, merges or worktree cleanup without the owner asking, and subagents go through the model routing table.
