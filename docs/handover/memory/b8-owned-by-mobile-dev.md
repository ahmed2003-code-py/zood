---
name: b8-owned-by-mobile-dev
description: "B8 (Kotlin POS) is built and owned by a separate mobile developer, not by this session or the owner"
metadata:
  node_type: memory
  type: project
  originSessionId: a8008b01-f582-47d7-92c9-f8bbcc1da429
  modified: 2026-11-19T12:43:46.738Z
---

On 2026-11-19 the owner said B8, the Kotlin POS on real hardware, will be built by a mobile developer working with them, who is responsible for it. The owner does not do it, and neither do we.

**Why:** a separate person owns the native POS app and the hardware spike.
**How to apply:** do not plan or build B8 code here. Our side of B8 is the contract the developer builds against: the B4/B5 POS protocol (`docs/architecture/b4-fueling-contract.md`, `b5-sync-contract.md`), `docs/architecture/pos-and-driver-app.md`, the OpenAPI document, and the `rules` module semantics. Keep those accurate and stable, and treat any change to the POS endpoints as a breaking change for that developer (bump `posContract`). See [[zawed-production-plan]].
