---
name: fable-unavailable
description: Fable is no longer available to this user (2026-11-19); zawed-deep and zawed-reviewer run on Opus
metadata:
  node_type: memory
  type: feedback
  originSessionId: a8008b01-f582-47d7-92c9-f8bbcc1da429
  modified: 2026-11-19T12:13:06.424Z
---

On 2026-11-19 the owner said Fable is no longer available at all ("فابل بقي غير متاح خلاص").
`.claude/agents/zawed-deep.md` and `zawed-reviewer.md` were switched to `model: opus`, and the
model-routing table in CLAUDE.md now says Opus for those rows.

**Why:** Fable calls fail. Earlier sessions also lost work to Fable quota and credit limits.
**How to apply:** never pass `model: fable` and never wait on a Fable reset. Keep using the
separate `zawed-deep` and `zawed-reviewer` agents (on Opus) for money and security work and its review, so that work still gets a dedicated brief and an independent review. Older notes that say "Fable review pending" mean "review pending (now on Opus)". See [[zawed-production-plan]].
