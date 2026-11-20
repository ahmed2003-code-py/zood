# Runbooks

Procedures for running ZAWED. They were written in B7 and are kept current by `zawed-docs-sync`. Each one says when to use it, the exact steps, and how to tell that it worked.

| Runbook | Use it when |
|---|---|
| [Deploy and roll forward](deploy.md) | You ship a change to `zawed-dev` (and, later, production), or a deploy failed |
| [Backup and restore](backup-and-restore.md) | You take a backup before a risky change, or data must be brought back |
| [Incident response](incident.md) | Something is down, wrong or suspicious |
| [Secrets and devices](secrets-and-devices.md) | A secret leaked, or a POS device is lost or stolen |
| [Onboarding a pilot company](pilot-onboarding.md) | A new fleet company or station joins |

**Rules that always apply:**
- `master` and the Railway service `zood-web` are the frozen investor demo. No runbook touches them.
- Every Railway command names its project, environment and service explicitly.
- Database checks run inside `BEGIN READ ONLY` unless the runbook says it writes.
