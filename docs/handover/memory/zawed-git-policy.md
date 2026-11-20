---
name: zawed-git-policy
description: master + Railway investor demo are frozen; all work on develop/feature branches; never merge to master or redeploy without explicit approval
metadata:
  node_type: memory
  type: feedback
  originSessionId: 67969005-0512-4044-824c-33996a086771
  modified: 2026-11-13T09:29:23.312Z
---

`master` (tag `investor-demo-v1` = b864d1c) is the approved investor demo, deployed on Railway project "zood", service "zood-web" (https://zood-web-production.up.railway.app). Never commit/push to master, never merge develop into master, never `railway up` to zood-web, never change its source branch. Work on `develop` (or `feature/*` from develop), commit and push there. A future online dev environment must be a separate service (e.g. zood-dev).

**Why:** the user froze the investor demo on 2026-10-13 and wants it untouched until they explicitly say "Merge develop into master."
**How to apply:** run `git branch --show-current` before any work; before a master merge, stop and ask, then run typecheck, lint, unit tests, build, E2E and a manual investor-flow regression. The زوّد / ZAWED rebrand (d787d06) was merged to master on 2026-10-15 with the user's approval (fast-forward; tag still at b864d1c). Railway was NOT redeployed then (deployed via `railway up`, not GitHub), so the live demo still shows ZOOD until the user approves a redeploy. See [[zood-project-context]].

Update 2026-11-12: the demo service in project `zood` is now named `Zawed` and was found Offline (404 "Application not found"). Both of its deployments were REMOVED, the latest dated 2026-10-14; it was not redeployed and was left for the user to decide. `apps/web` is linked to `zood`, so never run `railway up` there without `--project`. The dev environment is a SEPARATE Railway project `zawed-dev` (id 5e712b7f-0503-4cb6-9024-349c62f6e57c; services Postgres, zawed-api, zawed-web), created with the user's approval; `apps/api` is linked to it. Always deploy with explicit `--project/--environment/--service`.

Update 2026-11-13: deploy only from a clean worktree of the commit (`git worktree add --detach <scratchpad>/deploy-wt <sha>`, then `railway up` from its apps/api or apps/web). `railway up` uploads the working folder as-is: a deploy from the live tree once shipped agents' unfinished files (the web build failed; the previous deployment stayed live). zawed-dev is now Railway Infrastructure as Code (`.railway/railway.ts` at the repo root, which is linked to zawed-dev; railway.json files removed from develop). Run `railway config plan/apply` from PowerShell with `$env:_` set to `%APPDATA%\npm\node_modules\@railway\cli\bin\railway.exe` (the SDK finds the CLI via `_`; Git Bash's value breaks it), and read the plan: unknown keys are silently ignored (deploy settings go under `deploy: {...}`).
