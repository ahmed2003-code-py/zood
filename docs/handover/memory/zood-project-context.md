---
name: zood-project-context
description: "ZOOD fleet-fuel platform — investor demo state after the overnight session (D0–D8 largely done), approved decisions, where docs live"
metadata:
  node_type: memory
  type: project
  originSessionId: 67969005-0512-4044-824c-33996a086771
  modified: 2026-11-12T13:03:44.737Z
---

ZOOD, rebranded زوّد / ZAWED on 2026-10-15, is a B2B fleet fuel management platform (Egypt). Repo: github.com/ahmed2003-code-py/zood. Source of truth: Fleet_Fuel_Management_PRD_Final__v6.pdf (user-provided, not in repo; a copy is in the user's Downloads folder). User writes Egyptian Arabic + English prompts. The production build plan is tracked in [[zawed-production-plan]].

State (2026-10-13, after an autonomous overnight session): the investor prototype in apps/web covers the full story — POS (login, simulated RFID scan, verify, fuel, confirm, receipt, cooldown, limit decline, print failure, offline queue + auto sync), live cross-window Company updates, Company/Station/Ops screens, alert investigation, presenter panel, /demo launcher. Runbook: docs/demo/investor-demo.md; decisions: docs/demo/overnight-decisions.md; known env issue: docs/architecture/known-issues.md (intermittent ERR_NETWORK_ACCESS_DENIED in local Playwright).

Approved decisions:
- Demo phases D0–D8 (not the PRD's Phase 0–4). Production backend (Spring Boot, Postgres, Flyway) and Kotlin POS only after demo approval.
- Arabic RTL + English LTR and Light/Dark/System are core; no hardcoded strings; semantic tokens only; no dark: variant; Western digits; plates never transliterated; charts always plot LTR.
- Budgets (department monitoring, never block) vs Limits (per-vehicle cap) stay separate; company data view-only (requests).
- No authorization holds in the demo; prepaid wallet only; stations owned by operators.
- Receipt compact (~7–10 cm), receiptLocale stored on the transaction; POS defaults to Light.

**Why:** investor/funder presentation is the near-term goal; the user prefers controlled phases with approval, but authorized autonomous overnight work once.
**How to apply:** before new work, read docs/demo/*.md; keep the core demo flows green (e2e/fueling.spec.ts); state objective/files/assumptions before a phase unless told to work autonomously.
