# ZAWED Roadmap Delta

## Executive Decision

**Keep the ZAWED plan.**

PetroApp's authenticated Station Panel and its public material **validate** our core decisions:

- prices are set centrally, not by stations;
- tags are managed by Operations;
- B2B company balances are prepaid;
- the attendant is attributed on every fueling;
- the platform is one system for companies, stations and operations.

The same evidence exposes **station-side and commercial gaps** that our PRD does not cover.

**Proposed delta:**

1. **Investor demo: no change.** It stays frozen. Only the talk track is updated (§4).
2. **Pilot MVP: four additions, each blocking the pilot:**
   1. a **station financial account with payout records**;
   2. a **legal decision on invoicing**, plus the minimum documents it requires;
   3. **effective-dated fuel prices propagated to devices** (moved earlier from PRD Phase 2);
   4. **station-account security** (MFA, and controlled changes to bank details).
3. **Production V1: adapted additions:**
   - conditional fueling evidence (pump photo);
   - numeric odometer with an optional photo (moved earlier from Phase 3);
   - report sets with asynchronous exports;
   - the agent lifecycle;
   - a station self-service boundary;
   - disputes and adjustments, without ever "rejecting" a completed fueling;
   - the full invoicing module.
4. **Downgraded:** driver identity (P1 → P2, research) and company self-service (keep read-only for the pilot).
5. **No ZAWED advantage is confirmed yet.** Six are *potential*. Investor messaging must describe how ZAWED is designed, never what PetroApp lacks.

**Evidence base:**

- [petroapp-vs-zawed.md](petroapp-vs-zawed.md) v0.2 (public research + authenticated Station Panel observation);
- PRD v6;
- approved decisions;
- the prototype.

**Precedence:** approved decisions > roadmap/architecture docs > PRD v6 > prototype.

Every change to an approved or PRD decision below is shown as **OLD → NEW → WHY**. Nothing here is approved until the product owner signs off.

---

## 1. What We Learned

1. **Stations live by their statement and their payout.**
   - PetroApp gives stations a running ledger: daily sales, daily fees, frequent bank transfers and a balance.
   - Payouts arrive near-daily (22 transfers in one month at the observed station).
   - Our plan has only period settlement summaries with "Mark as Paid."
2. **Evidence capture at the pump is accepted practice.** The fueling records carry a pump photo and an odometer photo. A numeric odometer value was **not** observed.
3. **Two-sided monetisation is plausible.**
   - Fleets: per-vehicle subscription (public).
   - Each fueling record: a 0.05 EGP-per-liter fee, about 0.24% of value and about 3 EGP per fueling (authenticated). Who bears it is unknown.
4. **Commercial paperwork is part of the product.** Per-fueling "invoices", monthly aggregated invoices (inaccessible to the station account), printable fee invoices, and tax and commercial-registration data on the station profile.
5. **Stations get broad self-service.** They manage branches, staff and their bank profile. Prices, chips and vehicles stay out of their hands.
6. **Fleet fueling is diesel-heavy.** Diesel is about 90% of value at the observed station; the average fueling is about 61 L and about 1,250 EGP. Truck-scale limits and tank-capacity rules matter.
7. **There is a legacy surface to avoid:**
   - pages that return 419;
   - an "All"-only permission list;
   - no invoice detail page and only a 3-month history;
   - Excel-upload configuration;
   - modules carried over from Saudi Arabia.

### Re-evaluation of the v0.1 "top gaps"

| v0.1 gap | Genuinely missing? | Already planned? | Authenticated evidence | Priority change |
|---|---|---|---|---|
| E-invoicing | **Yes**: no invoicing domain in the PRD | No | **Strengthened**: invoice documents and tax profile exist; e-invoice certification is still public-only | P0 (decision before the pilot; module in V1) |
| Driver identity | Yes, not planned | No | **Weakened**: no driver anywhere in the station view; attendant attribution matches ours | P1 → **P2 / research** |
| Odometer | Planned late (Phase 3, health score only) | Yes (late) | **Strengthened**: an odometer photo on most records in Egypt | Phase 3 → **V1** (P1) |
| Reports | No; planned in PRD Phase 1. A prototype gap only. | Yes | **Strengthened**: station reports and exports are mature; our station reports are unspecified | P1; add station report set + asynchronous exports |
| Company self-service | Deliberate design (read-only + requests) | Yes (opposite approach) | **Weakened**: the company side was not observed. The evidence concerns **station** self-service. | P1 → **P2** (revisit after the pilot); station boundary is the new P1 |

## 2. What Changes

Each change lists: current plan · new evidence · recommended change · reason · priority · dependencies · relevance (Investor / Pilot / Production).

### MOVE EARLIER

**M1. Fuel price schedule + device propagation**

| Field | Detail |
|---|---|
| Current plan | Ops sets each station's price per fuel type, with a stale-price indicator (PRD §6.1). The propagation service is PRD Phase 2. |
| New evidence | No price control in the station panel; uniform prices per fuel type across all records. Consistent with nationally regulated pricing [INF, to confirm]. |
| **OLD DECISION** | Price propagation in PRD Phase 2 |
| **NEW RECOMMENDATION** | Pilot: a national reference price per fuel type with an **effective-from** time. An optional station override is Ops-only. Devices receive scheduled changes in advance and apply them by time, including offline. |
| **WHY** | A national price change applies to every station at once. A device charging a stale price misbills every fueling until it syncs. |
| Priority | P0 |
| Dependencies | Sync; device time synchronisation (PRD §11) |
| Relevance | Investor: low · Pilot: **blocking** · Production: required |

**M2. Odometer capture**

| Field | Detail |
|---|---|
| Current plan | PRD Phase 3: guided camera step; feeds the health score; never used for fraud or billing |
| New evidence | Odometer **photo** on most observed records. Numeric extraction not observed. |
| **OLD DECISION** | Phase 3; health score only |
| **NEW RECOMMENDATION** | V1: optional per company. Numeric entry on the keypad (fast), with an optional or conditional photo. Readings feed km/L analytics and **data-quality notices** to the fleet manager (decreasing reading, implausible km/L). Still never blocking, never billing, never a fraud decision. OCR stays later. |
| **WHY** | Efficiency is the fleet manager's core KPI. A photo alone gives evidence but no analytics. The market already accepts the step. |
| Priority | P1 |
| Dependencies | Evidence/media module (if photo); vehicle tank and class data |
| Relevance | Investor: medium (roadmap slide) · Pilot: optional · Production: V1 |

### ADD

**A1. Station financial account + payouts**

| Field | Detail |
|---|---|
| Current plan | `Station_Settlements` period summary, "Mark as Paid", settlement cycle config (PRD §6.3, §8.3) |
| New evidence | PetroApp station ledger: opening balance, daily sales totals, daily fees, frequent bank transfers, running balance (authenticated) |
| Recommended change | Station payable accounts in the **existing double-entry ledger**, separate from company wallets. Each completed fueling (including VIP) credits the station payable; fees post per the commercial model; payouts debit it with a bank reference. Settlement periods become **statements derived from the ledger**. The station sees a statement with drill-down; Ops records payouts and reconciles them. |
| Reason | Stations judge the platform on payout reliability and clarity. Auditors need payable ↔ payout ↔ bank traceability. |
| Priority | P0 |
| Dependencies | Ledger; commercial model (fee placement); VIP payer decision; verified bank details (A3) |
| Relevance | Investor: medium · Pilot: **blocking** · Production: required |

**A2. Invoicing domain**

| Field | Detail |
|---|---|
| Current plan | None (receipts and ledger only) |
| New evidence | Per-fueling invoice records, monthly aggregated invoices (419), printable fee invoices, tax/CR data (authenticated); "certified e-invoices" (public) |
| Recommended change | **Before the pilot:** a legal decision with a tax advisor on who sells fuel to whom, ZAWED's legal role, and e-invoice obligations under the Egyptian Tax Authority regime. **Pilot:** monthly statements (company consumption statement; station sales and payout statement) plus whatever tax documents counsel requires. **V1:** a billing and invoicing module (fuel documents per the legal model; platform-fee invoices; e-invoice submission if required). |
| Reason | Finance teams will not adopt without compliant documents. This gap is in no ZAWED phase. |
| Priority | P0 (decision) / P1 (module) |
| Dependencies | Legal entity for wallet funds (PRD Phase 0); revenue model |
| Relevance | Investor: high (a credible "how money and tax work" answer) · Pilot: blocking (decision + minimum documents) · Production: required |

**A3. Station account security**

| Field | Detail |
|---|---|
| Current plan | Station Manager signs in with e-mail + password (PRD §10.2). Bank details are not modelled. |
| New evidence | Optional 2FA and device linking on PetroApp station accounts; a self-service bank-account form (authenticated) |
| **OLD DECISION** | Station Manager: e-mail + password |
| **NEW RECOMMENDATION** | MFA for station roles that see financials. Bank-detail changes require verification (bank letter), Ops approval and a payout hold window. All changes are audited. |
| **WHY** | Station accounts control where money goes. Changing the bank details is the highest-value fraud path. |
| Priority | P0 (bank-change control) / P1 (MFA) |
| Dependencies | Identity; audit; A1 |
| Relevance | Pilot: blocking (bank control) · Production: required |

**A4. Disputes and adjustments**

| Field | Detail |
|---|---|
| Current plan | Two-person reversal = immutable credit note (PRD §6.4). Sync conflicts are committed and alerted. |
| New evidence | PetroApp shows "pending" and "rejected" invoice states to stations; workflow unknown (authenticated) |
| Recommended change | **Keep finality.** Add a `Dispute` case (raised by the company, station or Ops, with evidence attached), resolved only by a **reversal**, a two-person **adjustment** (partial correction), or no change. Never a "rejected" state on a completed fueling. |
| Reason | Fuel has physically left the pump. Stations need payment certainty, and finance needs auditable corrections. See Appendix J. |
| Priority | P1 (V1) |
| Dependencies | Ledger; evidence; support |
| Relevance | Investor: medium (finality principle) · Pilot: via ticket + reversal · Production: V1 |

**A5. Station support tickets**

| Field | Detail |
|---|---|
| Current plan | Tickets for companies only |
| New evidence | Station tickets with categories (including chips and installation) and WhatsApp support |
| Change | Station ticket types: device fault, tag-read issue, payout query, price query, dispute |
| Priority | P2 (V1) |

### ADAPT

**D1. Conditional fueling evidence (pump photo)**

| Field | Detail |
|---|---|
| Current plan | Pump visual matching (OCR) in PRD Phase 2, non-blocking |
| New evidence | Pump photo on fueling records (authenticated) |
| **OLD DECISION** | Pump capture only as OCR visual matching in Phase 2 |
| **NEW RECOMMENDATION** | V1: an **evidence policy** per company (off / risk-triggered / sampled / always). Photo only; OCR matching stays Phase 2. Default: risk-triggered plus a small random sample. Never blocking; queued offline. |
| **WHY** | Strong dispute value at low engineering cost. Conditional capture keeps the pump fast. See Appendix A. |
| Priority | P1 |
| Dependencies | Evidence/media module; policy engine; device camera timing (hardware spike) |
| Relevance | Investor: medium · Pilot: optional (measure friction) · Production: V1 |

**D2. Station self-service boundary**

| Field | Detail |
|---|---|
| Current plan | Station onboarding, devices and agents are Ops-only; the Station Manager mostly views |
| New evidence | PetroApp stations add, edit and delete branches and logins, and manage workers and bank details |
| **OLD DECISION** | Ops-only |
| **NEW RECOMMENDATION** | A tiered model (Appendix G). Pilot stays Ops-run. |
| **WHY** | Ops cost at scale, without giving up data integrity |
| Priority | P1 (V1) |

**D3. Agent lifecycle**

| Field | Detail |
|---|---|
| Current plan | Worker ID + PIN set by Ops; lockout; single session; all roles Active/Suspended/Deactivated (PRD §3) |
| New evidence | Worker transfer with audit log; deactivation; "connected" state |
| Change | Agent–station assignment history; the station manager can **suspend immediately** and transfer agents within their operator's stations (audited); new agents by request + Ops approval; minimal personal data. See Appendix F. |
| Priority | P1 (V1) |

**D4. Reports and exports**

| Field | Detail |
|---|---|
| Current plan | Date-range export by tier, vehicle and station (company); station reports unspecified |
| New evidence | Mature station reports; Excel everywhere; e-mail for large exports |
| Change | Defined report sets (Appendix H). XLSX/CSV/PDF. **Asynchronous export jobs** with an in-app download link. No chart-image exports. |
| Priority | P1. Pilot: transaction exports + station statement. V1: full sets. |

**D5. Fueling schedules**

| Field | Detail |
|---|---|
| Current plan | Limit rules with allowed days/hours; time-of-day restrictions in Phase 2 |
| New evidence | Excel-uploaded "vehicle shifts" (meaning inferred) |
| Change | Model as **schedules in the policy engine** (per vehicle or group). Optional CSV bulk assignment. No Excel-first workflow. See Appendix I. |
| Priority | P2 (V1.5) |

### MOVE LATER

**L1. VIP tier in the pilot**

| Field | Detail |
|---|---|
| **OLD DECISION** | VIP is in the PRD Phase 1 scope |
| **NEW RECOMMENDATION** | Exclude VIP from the pilot unless the payer is decided |
| **WHY** | VIP fuel is owed to the station with no wallet debit. Paying stations on a schedule (A1) turns this into working-capital exposure. |
| Priority | — (scope) |
| Relevance | Pilot |

**L2. Plate OCR in the pilot**

| Field | Detail |
|---|---|
| **OLD DECISION** | Plate OCR is a PRD Phase 1 deliverable |
| **NEW RECOMMENDATION** | Not pilot-blocking. Deliver in V1 (configurable, default off). |
| **WHY** | Scope and hardware risk. Conditional evidence (D1) covers disputes. This is a scope judgement, **not** competitor-driven. |
| Priority | P2 |

### RESEARCH BEFORE DECIDING

- **R1. Driver identity per fueling.** The fleet-side walkthrough must establish whether PetroApp Egypt records drivers. Interview pilot fleets about pooled-vehicle accountability. P2.
- **R2. Company self-service.** Revisit the approved read-only model after pilot feedback. P2.
- **R3. Per-trip allowances.** Public evidence only; validate with pilot fleets. P2.

### DO NOT BUILD

See §13.

## 3. What Does NOT Change

These decisions are validated or unchallenged by the evidence:

| Decision | Evidence |
|---|---|
| Stations never set fuel prices | Validated: not station-controlled at PetroApp |
| Tags and chips managed by Operations, not stations | Validated: chips handled through support at PetroApp |
| B2B prepaid company balance; no cash, no individuals | Validated: all observed sales were company, from balance |
| Attendant (agent) attribution on every fueling | Validated |
| Station operators own stations (ZAWED does not own stations) | Consistent with the evidence |
| One platform spanning company, station and operations | Validated |
| Transaction finality; corrections by reversal (two-person) | Kept deliberately (Appendix J) |
| Offline-first POS, tag-level cooldown, idempotency, receipt fail-safe | Unchallenged |
| Budgets (monitor) ≠ limits (block); company panel read-only + requests (for the pilot) | — |
| No authorization holds; compact receipt with `receiptLocale`; bilingual + Light/Dark/System; semantic tokens | — |
| Architecture: Spring Boot modular monolith, PostgreSQL + RLS, Redis, object storage, Flyway; Kotlin POS after the hardware spike | — |
| `master` and the Railway investor demo stay frozen | — |

## 4. Investor Demo Decision

**No change to code, UI, data or flows.** The demo stays frozen at `investor-demo-v1` / current `master`.

**Talk-track additions** (verbal only, no build):

- **"How do you compare to PetroApp?"**
  > PetroApp proves the market: fleets pay from a balance, stations are paid on a schedule, and evidence is captured at the pump. ZAWED is designed around transaction integrity at the pump: offline operation with visible sync, cooldown on the tag, final transactions with auditable corrections, and one live system for fleets, stations and operations. Our pilot adds station statements and payouts, compliant invoicing, and conditional evidence capture.
- **Roadmap slide (optional):** station statement and payouts · invoicing · evidence and odometer · price schedules.
- **Do not say** "PetroApp doesn't support offline / cooldown / audit." None of that is confirmed.

## 5. Pilot MVP

**Assumptions:**

- one station operator with one to three stations;
- two to five fleet companies, Normal tier;
- real money and real terminals;
- Ops runs onboarding.

### From the existing plan (PRD Phase 1 core)

| Area | Scope |
|---|---|
| Security | Multi-tenant backend, RBAC, MFA for Ops and Super Admin roles |
| Fueling | Transaction engine (idempotent, concurrency-safe); company wallet + immutable ledger; top-up by bank transfer + Ops credit |
| POS | Kotlin, on spike-validated hardware: login (ID + PIN, lockout, single session); NFC scan with tag-level cooldown; verify; fuel; confirm; receipt fail-safe; offline queue + sync; device binding and revocation |
| Policy | Per-vehicle limits (day/week/month, L/EGP); low-balance alerts |
| Ops | Onboarding of company, vehicle, tag, station, device and agent; tag lifecycle; alert queue; sync monitor; reversal (two-person); audit log |
| Company portal | Dashboard, transactions (offline origin and sync times), vehicles, wallet ledger, transaction export, requests/tickets |
| Station portal | Transactions, devices (view), shifts (view) |
| Operations quality | Reconciliation job (ledger vs cached balance) |

### Added by this delta (pilot-blocking)

- **M1** price schedule + device propagation;
- **A1** station financial account, payout records and station statement;
- **A2** invoicing legal decision, plus monthly statements and any counsel-required tax documents;
- **A3** bank-detail change control (MFA for station financial roles recommended);
- **D4 (minimum)** transaction exports (company, station) and the station statement export.

### Explicitly not in the pilot

- VIP (L1);
- plate OCR (L2);
- evidence photos and odometer (optional experiment only);
- station self-service;
- dispute workflow (tickets + reversal suffice);
- driver app;
- dynamic limits;
- ERP.

## 6. Production V1

Everything in the pilot, plus:

**Remaining PRD Phase 1 items:**

- plate OCR (configurable);
- Super Admin on a separate auth domain;
- VIP with the safeguard (once the payer is decided);
- end-shift summary;
- settlement cycle configuration;
- soft-delete everywhere;
- retention jobs.

**Added by this delta:**

| Change | Item |
|---|---|
| A2 | Billing and invoicing module (fuel documents per the legal model, platform-fee invoices, e-invoice submission if required) |
| A3 | MFA for all station financial roles |
| A4 | Disputes + two-person adjustments |
| A5 | Station tickets |
| D1 | Conditional fueling evidence |
| M2 | Odometer, numeric + optional photo, with km/L |
| D2 | Station self-service boundary |
| D3 | Agent lifecycle |
| D4 | Full company and station report sets + asynchronous exports |

## 7. V1.5 (operational maturity)

- PRD Phase 2:
  - dynamic limits engine with **fueling schedules (D5)**;
  - transaction validation (push/OTP);
  - pump OCR visual matching (on top of D1);
  - station reconciliation (meter vs system, shift close, payout requests);
  - Accountant sub-users.
- Bank statement import and automatic payout matching.
- Alert channels (e-mail / WhatsApp digests).
- Thin driver PWA.
- Research outcomes: driver identity (R1), company self-service (R2).

## 8. V2+

- PRD Phase 3:
  - geofencing;
  - offline micro-limit hardening;
  - petty cash OCR;
  - Fleet Health Score;
  - unified anomaly dashboard.
- PRD Phase 4:
  - ERP connectors (on demand, after exports and a read API);
  - telematics;
  - tolls;
  - mobility services;
  - payment gateway.
- Station amenities and a station locator.
- Per-trip allowances (if R3 confirms demand).

## 9. Architecture Impact

This maps the delta onto the planned **Spring Boot modular monolith**, using Spring Modulith boundaries and events. No code.

### Modules

| Module | New / expanded | Responsibility | Depends on | Key events |
|---|---|---|---|---|
| **pricing** | New, extracted from network/stations | National and station price schedules, effective dates, propagation payloads | network, sync | `PriceScheduled` → device snapshot |
| **station-finance** | Expands settlement | Station payable accounts (in the ledger), payouts, statements, payout reconciliation | ledger, fueling, network, identity | consumes `FuelingCompleted`; emits `PayoutRecorded` |
| **billing-invoicing** | New | Invoice documents (fuel / fee), numbering, tax status, e-invoice submission adapter | ledger, station-finance, tenancy, reporting | `InvoiceIssued` |
| **evidence-media** | New | Capture-policy evaluation (with policy), media upload lane, object storage, retention, hashing | fueling, policy, sync, object storage | `EvidenceAttached` |
| **fleet** (odometer) | Expanded | Odometer readings, plausibility, km/L read model | fueling, evidence-media | `OdometerRecorded` |
| **workforce** | New, or inside network | Agent lifecycle, station assignment history, suspension | identity, network, audit | `AgentSuspended` → device session revocation |
| **policy** | Expanded | Fueling schedules; evidence triggers | fleet, pricing | — |
| **reporting-export** | Expanded | Report read models, asynchronous export jobs, files | all read models, notifications | `ExportReady` |
| **cases** (disputes) | Expands support | Disputes, links to reversals/adjustments | fueling, ledger, evidence-media | `DisputeResolved` |

### Boundary rules

1. Only **ledger** writes money.
   - station-finance and billing-invoicing *request* postings;
   - corrections are compensating entries;
   - completed fuelings are never mutated.
2. The **sync** module carries two lanes from the POS: transactions first, media second. Media can never delay a financial record.

### Data model concepts (only what the domain requires)

| Concept | Purpose | Notes |
|---|---|---|
| `FuelPriceSchedule` | fuel type, scope (national/station), price, effective-from, set-by | Replaces a single "current price" field |
| Station payable **ledger accounts** | One per station within the existing double-entry ledger | **Not** a separate `StationLedgerEntry` store |
| `Payout` | station, amount, bank reference, status, recorded-by, paid-at | Optional `PayoutBatch` later |
| `StationSettlement` (existing) | Becomes a derived period statement | — |
| `Invoice` (+ lines) | document type (fuel / platform fee), issuer, recipient, period, tax status, e-invoice id | **One** entity with a type; no separate `TaxInvoice`. Shape decided after A2 research. |
| `FuelingEvidence` | transaction, kind (pump / odometer / plate), capture reason (policy / sample / risk / manual), media ref, hash, captured-at | — |
| `OdometerReading` | vehicle, transaction, km, source (manual / photo / OCR), plausibility status | — |
| `AgentAssignment` | agent, station, from, to, changed-by, reason | Covers transfers; no separate `WorkerTransfer` |
| `ExportJob` | requester, scope, filters, format, status, file ref, expiry | — |
| `Dispute` | transaction, raised-by party, reason, evidence refs, status, resolution → reversal/adjustment id | — |
| `TransactionCorrection` | Extends `Transaction_Reversals` with type reversal / adjustment; two-person rule | — |
| `StationBankAccount` | verified status, verification document, approved-by, effective-from (payout hold) | — |

**Not recommended:**

- a "rejected" transaction status;
- a separate tax-invoice table;
- a separate worker-transfer table;
- chart-image export jobs.

## 10. Commercial Learnings

This is business-model analysis only. **Do not copy PetroApp pricing.**

### Evidence

- A per-liter fee of 0.05 EGP on every fueling (≈ 0.24% of value; ≈ 3 EGP per fueling). The payer is unknown: it appears on station-side records and reduces the station's balance line [INF, weak].
- A per-vehicle fleet subscription plus chip and installation fees (public).
- 100% company-balance sales at the observed station.
- Near-daily station payouts.

### Options for ZAWED

| Model | Pros | Cons |
|---|---|---|
| Per-liter (fleet- or station-funded) | Scales with volume; simple to explain | Tiny unit value; station-funded fees compete with regulated, thin retail margins [INF] |
| Per-transaction | Predictable for stations; matches ZAWED's per-transaction integrity cost | Penalises small fuelings |
| Fleet subscription (per active vehicle) | Matches fleet value (control, savings); recurring revenue | Sales friction for small fleets |
| Hybrid (fleet subscription + small per-liter or per-transaction platform fee) | Two revenue lines; aligns both sides | More complex to explain and invoice |
| Enterprise contract | Large fleets, custom SLAs | Long sales cycles |
| Station-funded acquisition fee | Stations gain guaranteed B2B volume | Station resistance; margin pressure |
| Hardware pass-through (tags, installation) | Cost recovery | Not a margin business |
| Float income on prepaid balances | Potentially material | **Regulatory and legal research required** (licensing for holding funds); do not plan on it |

**Recommendation:**

- Decide before the pilot **only what invoicing needs:** who charges what to whom.
- Pilot with one **simple, transparent model**. Candidate: a fleet-funded per-vehicle subscription plus a small, clearly shown per-transaction or per-liter platform fee.
- Treat payout speed (e.g. T+1) as a station-side value lever, not a price.
- Resolve the **VIP payer** and **offline negative balance** questions first. Both create working-capital exposure under scheduled payouts.

## 11. Confirmed / Potential Differentiators

**Confirmed: none.** The PetroApp fleet, driver, POS and admin sides were not observed.

**Potential:**

1. Offline-first fueling with explicit sync visibility for every party.
2. Final transactions with auditable corrections (no post-fueling rejection).
3. A per-transaction detail and investigation timeline.
4. Station-visible device and sync health.
5. Long accessible history (vs 3 months in PetroApp's station view).
6. Statement drill-down (once A1 is built).

**Insufficient evidence:**

- tag-level cooldown;
- idempotency;
- tank-capacity anomaly detection;
- immutable company ledger;
- budget vs limit separation;
- bilingual UX;
- live updates;
- tenant isolation.

Full classification: [petroapp-vs-zawed.md §8](petroapp-vs-zawed.md#8-zawed-advantages-differentiator-classification).

## 12. PetroApp Strengths

### Product capability (authenticated)

- pump and odometer evidence;
- station ledger and payouts;
- station reports and exports;
- worker transfer audit;
- branch self-service;
- optional station 2FA;
- station tickets;
- tax and commercial profile with invoice documents.

### Product capability (public only)

- driver app;
- "any ERP" integration;
- certified e-invoices;
- per-trip limits;
- Saudi vehicle services.

### Market and operational maturity

- 600+ stations in Egypt;
- real fleet volume;
- near-daily payout operations;
- chip installation logistics;
- case studies;
- USD 50M in funding.

## 13. Things We Will Not Copy

1. Post-fueling "pending" or "rejected" states. Use disputes plus compensating entries instead.
2. Unverified self-service bank-detail changes; unapproved branch creation and branch logins.
3. Excel upload as the main configuration path.
4. "All"-only permissions; menus leading to 419 pages.
5. Foreign-market modules (Saudi national address, municipality licence) in the Egypt product.
6. Cash and individual (retail) payment modes in the B2B product.
7. A 3-month visible history limit; ledgers aggregated per day with no drill-down.
8. Chart-image exports as a goal; e-mailing financial files as attachments (send in-app links instead).
9. Storing national ID numbers without a legal requirement.
10. A consumer app, Saudi-specific fleet add-ons, our own services marketplace (now).

## 14. Open Questions

Prioritised research backlog:

| # | Question | Why we care | Needed before | How to answer |
|---|---|---|---|---|
| 1 | Who legally issues fuel invoices, and what are ZAWED's e-invoice obligations? | Blocks the pilot's money flow and documents | **Pilot** | Tax advisor; legal |
| 2 | Legal entity and licensing for holding prepaid fleet funds | Wallet legality; float | **Pilot** | Legal / financial regulation counsel |
| 3 | Settlement contract: payout frequency, fee placement, who bears offline negatives and VIP | Station account design (A1) | **Pilot** | Pilot station operator negotiation |
| 4 | Are Egyptian pump prices nationally uniform for all pilot fuel types (incl. CNG, 80)? | Price schedule design (M1) | **Pilot** | Official price announcements; pilot station |
| 5 | How does PetroApp identify vehicles at the pump (NFC, QR, plate)? | Positioning; tag decisions | Production | Fleet-side walkthrough; station interview |
| 6 | Does PetroApp work offline? Does it sync, and with what conflict handling? | Validates the main potential differentiator | Investor messaging (to strengthen); Production | Station attendant interview; fleet walkthrough |
| 7 | Cooldown or duplicate-fueling protection at PetroApp? | Differentiator status | Investor messaging | Same |
| 8 | Limits (types, liters vs EGP, per trip) and budgets (block vs monitor)? | Policy-engine scope | V1.5 | Fleet-side walkthrough |
| 9 | Who rejects invoices, why, and what happens financially? | Validates the finality principle as a station-side benefit | Production | Station interview |
| 10 | What do the monthly aggregated invoices contain, and who issues them? | Invoicing model reference | Pilot (nice to have) | Station with access; PetroApp sales material |
| 11 | Does the fleet side record the driver? | Driver identity (R1) | V1.5 | Fleet-side walkthrough |
| 12 | Who pays the 0.05 EGP/L fee? | Commercial benchmark | Pilot pricing | Station interview |
| 13 | Fleet-company portal and driver app capabilities in Egypt | Company self-service (R2); driver app timing | V1.5 | Fleet account walkthrough |
| 14 | How often are fuel prices updated, and how far in advance are they announced? | M1 lead time | Pilot | Market data |

## 15. Final Decision Matrix

| # | Item | Decision | Horizon | Priority | Changes an approved/PRD decision? |
|---|---|---|---|---|---|
| 1 | Investor demo | Keep frozen; talk-track only | Investor | — | No |
| 2 | Station financial account + payouts | ADD | Pilot | P0 | Extends PRD settlements |
| 3 | Invoicing legal decision + minimum documents | ADD | Pilot | P0 | New |
| 4 | Billing and invoicing module | ADD | V1 | P1 | New |
| 5 | Price schedule + device propagation | MOVE EARLIER | Pilot | P0 | **Yes** (PRD Phase 2 → pilot) |
| 6 | Bank-detail change control | ADD | Pilot | P0 | New |
| 7 | Station MFA | ADD | V1 (recommended pilot) | P1 | **Yes** (PRD §10.2) |
| 8 | Conditional fueling evidence | ADAPT | V1 | P1 | **Yes** (Phase 2 OCR-only → V1 photo policy) |
| 9 | Odometer numeric + optional photo, km/L | MOVE EARLIER + ADAPT | V1 | P1 | **Yes** (Phase 3 → V1; adds data-quality notices; rule "never fraud/billing" kept) |
| 10 | Report sets + asynchronous exports | ADAPT | Pilot (minimum) / V1 | P1 | Extends PRD §6.2 |
| 11 | Agent lifecycle | ADAPT | V1 | P1 | Extends PRD |
| 12 | Station self-service boundary | ADAPT | V1 | P1 | **Yes** (Ops-only → tiered) |
| 13 | Disputes + adjustments; no rejection state | ADD / KEEP finality | V1 | P1 | Extends PRD §6.4 |
| 14 | Station tickets | ADD | V1 | P2 | New |
| 15 | Fueling schedules | ADAPT (already Phase 2) | V1.5 | P2 | No |
| 16 | VIP in the pilot | MOVE LATER | V1 | — | **Yes** (Phase 1 → after the payer decision) |
| 17 | Plate OCR | MOVE LATER | V1 | P2 | **Yes** (Phase 1 → V1) |
| 18 | Driver identity | RESEARCH | V1.5 | P2 | No (was a v0.1 recommendation) |
| 19 | Company self-service | KEEP read-only; RESEARCH | Revisit after the pilot | P2 | No |
| 20 | Per-trip allowances | RESEARCH | V2+ | P2 | No |
| 21 | Amenities / station locator | LATER | V2+ | P3 | No |
| 22 | Consumer app, Saudi add-ons, services marketplace, cash/retail modes | DO NOT BUILD | — | P3 | No |

---

## Appendix: Detailed Evaluations

### A. Pump photo

| Dimension | Assessment |
|---|---|
| Fraud prevention | Moderate. Deters "phantom" or inflated fuelings and gives evidence of the dispensed volume. It does **not** prevent tag misuse; prevention stays with cooldown and limits. |
| Dispute resolution | High. Directly answers "how many liters were dispensed?" |
| Friction | Roughly 3–8 s per photo [estimate]. Always-on capture threatens the ≤ 1.5 s offline scan-to-receipt target unless it happens after confirmation. |
| Storage | ~150–250 KB per compressed photo [estimate]. At the observed volume (~1,100 fuelings per month per station) that is ~0.2–0.3 GB per station per month per photo type, with 2-year image retention (PRD §9). Modest. |
| Privacy | Low (pump display). The framing guide must avoid people and plates. Access restricted; retention enforced. |
| Offline | Queue on the device in a **media lane** after the transaction lane. Never blocks the receipt; uploads on reconnect. Device storage must fit 30 days (spike). |
| Options | Always required · risk-based · random sampling · suspicious only · optional |
| **Recommendation** | A per-company evidence policy. **Default: risk-based + a 5–10% random sample.** "Always" is available for fleets that ask. Risk triggers: manual tag entry, liters near or above tank capacity, offline with a stale snapshot, new agent or device, near-limit fueling, VIP safeguard proximity. Captured **after confirmation, before success**, consistent with the PRD placement of pump capture. |

### B. Odometer

- The authenticated evidence proves a **photo only**. Do not assume PetroApp extracts a numeric value.
- **Options compared:**
  - numeric manual entry: fast, analysable, can be mistyped;
  - photo: evidence, not analysable without OCR;
  - OCR: costly, error-prone on varied dashboards;
  - telematics: Phase 4.
- **Recommendation (V1):** optional per company:
  - numeric entry, with the photo optional or conditional;
  - validation: monotonic per vehicle, km since the last fueling vs liters, against plausible bands by vehicle class;
  - outputs: km/L per vehicle, department and fleet;
  - **data-quality notices** to the fleet manager.
- **Kept from the PRD:** never blocking, never billing, never a fraud decision. The Fleet Health Score stays later. OCR is later.

### C. Service fee model

See §10. Key learning: PetroApp likely monetises **both sides** (fleet subscription + per-liter fee). The per-liter fee is small (≈ 0.24% of value), so monetisation leans on volume and subscriptions. ZAWED should choose a model that keeps **invoicing simple and transparent** for both fleets and stations.

### D. Station ledger vs company wallet

These are two separate financial planes in one double-entry ledger:

- **Company wallet:**
  - top-ups credit it;
  - billable fuelings debit it;
  - VIP posts nothing;
  - status shown on the POS as a word only.
- **Station financial account:**
  - fuelings (including VIP) create a **payable to the station**;
  - platform fees post according to the commercial model;
  - payouts settle the payable with a bank reference;
  - corrections are compensating entries.
- **Views:**
  - **Station statement:** opening balance, per-transaction lines (with daily subtotals), fees, payouts, closing balance; drill-down to each transaction and payout.
  - **Settlement statement:** period summary derived from the ledger (replaces the stand-alone PRD settlement record).
  - **Reconciliation view (Ops):** payouts vs bank references; mismatches flagged.

  A separate **payout ledger** is unnecessary: payouts are entries in the same ledger.

### E. Bank settlement

| Area | Implication |
|---|---|
| Station liquidity | Stations expect frequent payment; near-daily payouts are the market reference. Payout frequency is a selling point. |
| Working capital | With prepaid fleet balances, ZAWED pays stations from funds already collected. The exceptions are VIP fuel (no debit) and offline negative balances. Exposure is bounded only if those two are resolved. |
| Reconciliation | Each payout needs a bank reference. Pilot: manual entry by Ops. V1.5: bank statement import and matching. Direct bank APIs: later, if justified. |
| Settlement periods | Configurable per operator (daily T+1 default for the pilot), with statements per period. |
| Payout status | Scheduled → sent → confirmed (bank reference) → reconciled; visible to the station. |
| Pilot operations | Ops runs payouts manually from a payout list; dual control on payouts above a threshold. |

**Caveat:** we do not assume PetroApp's legal settlement structure.

### F. Worker (agent) management

| Capability | Recommendation |
|---|---|
| Agent lifecycle (active / suspended / deactivated) | **Yes.** Already PRD §3; make it station-visible. |
| Station assignment | **Yes.** `AgentAssignment` history. |
| Transfer history | **Yes.** Audited, within the same operator's stations. |
| Suspension | **Yes.** The station manager can suspend **immediately** (safe direction); this revokes device sessions. |
| Creation | Request + Ops approval (credentials and identity integrity) |
| Shift assignment | Later (PRD Phase 2 shift close / reconciliation) |
| Device assignment | **No fixed assignment.** Record the device per session/transaction (already planned). |
| Personal data | Minimal: worker ID, name, phone. National ID only if legally required. |

### G. Branch / station self-service

| Change | Station self-service | Request + Ops approval | Ops-only |
|---|---|---|---|
| Display info, amenities, opening hours, contacts | ✔ | | |
| Suspend agent; transfer agent within the operator | ✔ (audited) | | |
| New agent | | ✔ | |
| New station/branch; location (GPS); fuel types sold | | ✔ | |
| Station manager / extra user accounts | | ✔ | |
| Bank account details | | ✔ (verification letter + payout hold) | |
| Fuel prices | | | ✔ |
| Device activation, binding, revocation | | | ✔ |
| Tags; settlement terms; connectivity profile | | | ✔ |

**Rationale:** self-service where an error is cheap and reversible; approval where it affects money, location trust or credentials; Ops-only where it controls fraud surfaces.

### H. Exports

| Persona | Needed exports |
|---|---|
| Company accountant | Transactions (XLSX/CSV); wallet statement (XLSX/PDF); monthly consumption pack (PDF + XLSX); per-vehicle and department reports (XLSX) |
| Station | Transactions (XLSX); station statement (XLSX/PDF); payout/settlement statement (PDF); sales by fuel type and period (XLSX) |
| Ops | Ad-hoc CSV of any list |

- **Large ranges:** asynchronous `ExportJob` with an in-app notification and an expiring download link behind login. No financial attachments by e-mail.
- **Skip:** chart PNG/SVG export; "print invoices" separate from the PDF documents.

### I. Vehicle shifts

- **PRD coverage:** `Limit_Rules` already carries *allowed days/hours*, and Phase 2 adds time-of-day restrictions evaluated in the station's timezone.
- **Proposed model:** **fueling schedules in the policy engine:** named schedules (e.g. "Night shift 20:00–06:00, Sun–Thu") assigned to vehicles or groups.
- **Bulk assignment:** optional CSV.
- **Behaviour:**
  - evaluated at the pump, offline included (part of the snapshot);
  - outside the window = decline with a clear reason.
- **Caveat:** PetroApp's "shifts" meaning is INFERRED; validate with pilot fleets whether time windows are a real need before V1.5.

### J. Invoice review / rejection

**Question:** should a successful ZAWED fueling ever become "rejected"?

**No.**

- Fuel has been dispensed and the station is owed payment.
- A rejection after the fact shifts risk to the station, undermines trust in the platform, and makes the ledger mutable in practice.

**ZAWED states:**

| State | Meaning |
|---|---|
| Declined | Before completion; nothing dispensed |
| Completed | Final |
| Pending sync | Device-side only; committed at sync, never reversed unilaterally (PRD §7.5) |
| Reversed | Through a separate immutable correction record |

**Corrections:**

| Correction | Mechanism |
|---|---|
| Full reversal | Credit note, two-person (existing) |
| Partial adjustment | E.g. wrong liters entered; two-person |
| Dispute | A case that ends in one of the above, or no change |

All of these are compensating ledger entries. If a fleet distrusts a station, the remedy is a **policy** (evidence "always" at that station, station allow-lists), not a payment rejection.
