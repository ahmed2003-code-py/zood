# PetroApp vs ZAWED (زوّد): Competitive Product Audit

**Edition:** v0.2, 2026-10-15. This edition reconciles two evidence sources:

1. **Public research** (v0.1 of this report): PetroApp's websites, app-store listings and press.
2. **Authenticated observation** of the PetroApp Egypt **Station Panel** (`app.petroappeg.com`). The source is `petroapp-authenticated-product-observation.md`, supplied by the project owner. It was produced from a manual, read-only session on 15 Oct 2026. That file and its screenshots are held by the owner and are **not stored in this repository**.

**Scope of the authenticated evidence:** **one station-operator account with one branch.**

- **Not observed:** the fleet-company portal, the driver app, the attendant (POS) app and PetroApp's internal admin.
- **Not observed in a station account ≠ not supported by PetroApp.**

**Data handling:** the observed station's name, bank details, staff and personal data are deliberately left out. Only aggregate figures and ratios are used.

**Decision document:** see [zawed-roadmap-delta.md](zawed-roadmap-delta.md).

### Evidence labels

| Label | Meaning |
|---|---|
| **AUTH-OBS** | Authenticated: observed in the Station Panel |
| **AUTH-ACT** | Authenticated: visible action (exists, not executed) |
| **PUB-EG / PUB-SA** | Publicly documented (Egypt / Saudi materials; Saudi may not apply to Egypt) |
| **INF** | Inferred |
| **NOT-OBS-STN** | Not observed in the station account |
| **N/A-STN** | Not accessible to a station account (e.g. HTTP 419, or it belongs to the fleet side) |
| **UNK** | Unknown |

Authenticated evidence overrides earlier inference where they conflict.

---

## Decision summary

### Top 5 things PetroApp taught us

1. **Station economics are visible and operational.** Stations see a running financial statement: daily sales totals, a daily service fee and frequent bank payouts from PetroApp [AUTH-OBS]. ZAWED's settlement design is a period summary; it needs a **station financial account (ledger)**.
2. **Evidence photos are normal in this market.** The fueling records carry a **pump photo and an odometer photo** [AUTH-OBS]. This is a market precedent for evidence capture at the pump, accepted by real stations.
3. **Monetisation reaches the station side.** A **0.05 EGP-per-liter service fee** appears on every fueling record [AUTH-OBS]; fleets also pay per-vehicle subscriptions [PUB-EG]. Who bears the per-liter fee is UNK.
4. **Commercial and tax paperwork is built in.** Stations hold tax and commercial-registration numbers, there are monthly aggregated invoices (inaccessible to this account), and service-fee invoices can be printed [AUTH-OBS / N/A-STN]. Our PRD has no invoicing domain.
5. **Station self-service is broad and loosely controlled.**
   - Stations can add, edit and delete branches, create branch logins, transfer and deactivate workers, and edit bank details [AUTH-ACT].
   - Fuel prices and chips are outside their control [NOT-OBS-STN].

   It is useful convenience, but it carries data-integrity risk that ZAWED should not copy as-is.

### Top 5 ZAWED potential advantages

None is *confirmed*. Each is **potential**, because the PetroApp fleet, driver, POS and admin sides were not observed.

1. Offline-first fueling with **visible sync state** for every party. The station panel shows no offline or sync indicators at all.
2. **Final, guaranteed transactions.** No "pending" or "rejected" state after a completed fueling; corrections go through reversals. PetroApp shows pending and rejected invoice states to stations.
3. **Per-transaction auditability.** ZAWED has a full detail view and an investigation timeline. PetroApp's station panel has no invoice detail page and only a 3-month history window.
4. **Station device and sync health** visible to the station. Not present in the PetroApp station panel.
5. **Tag-level cooldown and a hard-limit/soft-flag policy model.** Unverifiable in PetroApp (fleet side).

### Top 5 PetroApp advantages

1. **Market maturity.** A live station network, real fleet volume, funding [PUB].
2. **Working station payout operations** (near-daily bank transfers) [AUTH-OBS].
3. **Evidence capture at the pump** (photos) [AUTH-OBS].
4. **Reporting and exports in production:**
   - Excel exports, with large exports sent by e-mail;
   - branch sales matrix;
   - service-fee report;
   - charts with CSV/PNG/SVG export [AUTH-OBS/ACT].
5. **Commercial and tax readiness** (tax/CR profile, invoice documents) and **optional 2FA** on station accounts [AUTH-OBS].

### Top 5 features we should NOT copy

1. A post-fueling **"rejected invoice"** state.
2. **Unverified station self-service** for bank accounts and new branches.
3. **Excel-upload workflows** as the primary configuration path (e.g. vehicle shifts).
4. **Coarse "All"-only permissions**, and menus that lead to 419 errors.
5. **Cash and individual (retail) payment modes**, and markets/modules carried over from another country.

---

## Corrections after authenticated observation

| # | Previous claim (v0.1) | New evidence | Corrected conclusion | Impact on ZAWED decision |
|---|---|---|---|---|
| 1 | **Invoicing:** "Certified e-invoices; PetroApp ahead" [PUB-EG] | Each fueling is a numbered "invoice" record with no tax fields; monthly aggregated invoices exist but return 419; service-fee invoices are printable; tax and commercial-registration numbers are held in the station profile [AUTH-OBS / AUTH-ACT / N/A-STN] | PetroApp runs an **invoicing domain** (per-fueling records, monthly aggregation, fee invoices). **E-invoice certification remains PUBLIC only**: no tax-authority status was visible. | Gap confirmed, and stronger: ZAWED needs a billing/invoicing domain. The legal model (who invoices whom) must be researched **before the pilot**. |
| 2 | **E-invoicing** treated as one thing | Station data shows *fuel* documents and *fee* documents are separate | Two invoice streams exist: **fuel** (station ↔ fleet, via the platform) and **platform fee**. The issuer is UNK. | ZAWED must design both streams separately. |
| 3 | **Odometer:** "Odometer/distance tracking [PUB-SA]" | An **odometer photo** is attached to most fueling records in Egypt. No numeric value is visible. [AUTH-OBS] | Odometer **evidence** is live in Egypt. Numeric extraction is **not proven**. | Strengthens moving odometer capture earlier. Do not assume OCR. |
| 4 | **Driver identity:** "PetroApp stronger (Drive code) [PUB-SA]" | No driver field anywhere in the station view; the **worker (attendant)** is recorded per fueling [NOT-OBS-STN] | Driver identity is **PUBLIC (Saudi) only**; the Egypt fleet side is UNK. Attendant attribution = SIMILAR to ZAWED's agent attribution. | **Weakened.** Driver identity drops from P1 to P2 / research. |
| 5 | **Station self-service:** not assessed (only company self-service was) | Stations can add, edit and delete branches with their own logins, fuel types, amenities and map; transfer and deactivate workers; manage users and groups; edit bank and tax profile [AUTH-ACT]. There is **no add-worker button**. | PetroApp gives stations **broad self-service** over structure and staff, but not over prices, chips or vehicles. | New input: define ZAWED's station self-service boundary (self-service / request + approval / Ops-only). |
| 6 | **Company self-service:** "PetroApp self-service posture [PUB]" | The company side was not observed | Unchanged: PUBLIC only | **Weakened** as a driver of change. Keep the approved read-only + requests model for the pilot; revisit with evidence. |
| 7 | **Reports:** "Per-vehicle reports [PUB]" | Station reports observed: sales list with totals, branch sales matrix (fuel type × cost/quantity), service-fee report, consumption charts, ledger; exports to Excel, e-mail for large exports, and chart CSV/PNG/SVG [AUTH-OBS/ACT]. **No per-vehicle report** in the station view. | Reporting maturity is real. Per-vehicle reports remain PUBLIC (fleet side). | Our PRD plans reports, but **station reports are under-specified**. Add a station report set and asynchronous exports. |
| 8 | **Station settlement:** "UNK" | No settlement module. A **ledger** shows an opening balance, daily invoice totals, daily fee totals, **near-daily bank transfers** and a running balance [AUTH-OBS]. | Settlement shows up as a **running station account plus frequent payouts**. The meaning of the balance (an advance position?) is INF. | ZAWED should model a **station financial account** on the ledger, separate from company wallets, with payout records and reconciliation. |
| 9 | **Station ledger:** not covered | The ledger is aggregated per day, has an empty attachments column, no transaction drill-down, and an Excel export [AUTH-OBS] | Daily aggregation only | ZAWED can do better: a statement with drill-down to each transaction and payout. |
| 10 | **Pricing:** not covered | No price field anywhere in the station panel; prices are uniform per fuel type across all records [AUTH-OBS, INF] | Prices are **not station-controlled**. This is consistent with nationally regulated fuel prices in Egypt [INF; to confirm]. | Validates the PRD rule that stations do not set prices. Suggests a **national price table with effective dates** and moving price propagation earlier. |
| 11 | **Workers:** not covered | Workers have status, a "connected" badge, branch transfer **with an audit log**, deactivation, national ID and employee number. **Creation is not possible from the station side.** [AUTH-OBS/ACT] | Worker lifecycle exists, with partial station control | ZAWED's agent model lacks a station-side lifecycle (suspend, transfer, history). Adapt. |
| 12 | **Branches:** "Branches (PUB-SA)" | Branch entity observed: one login per branch, fuel types (incl. 80-octane and CNG), amenities, map [AUTH-OBS] | Confirmed for Egypt (station side) | ZAWED has a station operator → stations model. Add amenities and display info later; keep fuel types and location under approval. |
| 13 | **NFC/chips:** "Chip per vehicle [PUB-EG]" | No chip, tag or card screens in the station view; there is a ticket category for "chip & installation problems" [NOT-OBS-STN, INF] | NFC is **PUBLICLY DOCUMENTED + NOT VISIBLE IN STATION ACCOUNT**. Chips appear to be managed by PetroApp through support. | No change. Our tag lifecycle (Ops) is consistent with this. |
| 14 | **Offline:** "LIKELY differentiator (not mentioned publicly)" | No offline, pending-sync or sync-time indicators in the station view [NOT-OBS-STN]. The attendant device is a camera-equipped mobile app [INF]. | **Still unproven either way.** The station view shows no sync visibility. | Remains **POTENTIAL**. Do not claim PetroApp lacks offline. |
| 15 | **Fraud controls:** "'Alert and stop' [PUB]; others not documented" | Station-visible controls: pump photo, odometer photo, worker attribution, plate + VIN, pending/rejected invoice states, worker transfer log, optional 2FA, device linking [AUTH-OBS] | PetroApp's visible fraud model leans on **evidence capture plus after-the-fact review**. Pre-authorisation controls are fleet-side and unseen. | ZAWED's prevention model (cooldown, limits, tag status) stays. Add **conditional evidence capture** as a complement. |
| 16 | **Additional services:** "Wash/oil/tires (PUB-SA)" | Only **branch amenity tags** (car wash, oil change, tires, ATM…), with no transactions or billing [AUTH-OBS] | In Egypt's station view, services are **discovery metadata**, not commerce | Keep services LATER. Amenity tags are cheap and useful for a future station locator (V2). |
| 17 | **Station terminal:** "Not documented" | The pump and odometer photos, phone-number usernames and the "connected" badge suggest a **mobile app** [INF]. No device management is visible. | The hardware model is still UNK | ZAWED's dedicated terminal plus device binding remains a design choice to validate (hardware spike). |
| 18 | **UX:** "Pending observation" | Observed: RTL Arabic, light/dark toggle, 419 errors on menu items, Saudi carry-over modules, "All"-only permissions, no invoice detail page, 3-month window, a misleading menu group, a new menu in progress [AUTH-OBS] | Known now (station side only) | Feeds "Things ZAWED should deliberately do better" (§11). |
| 19 | **Security:** "No public evidence" | Optional SMS/e-mail 2FA and a device-linking concept on station accounts [AUTH-OBS] | PetroApp offers 2FA to stations | Our PRD gives station managers e-mail + password only. **Add MFA for station accounts.** |
| 20 | **Business model:** "Per-vehicle SaaS + chip [PUB-EG]" | Plus a per-liter service fee on fuel records; every observed sale was a company sale paid from balance; cash and individual modes exist as filters [AUTH-OBS] | **Two-sided monetisation** (fleet subscription + per-liter fee) is likely. Who pays the fee is UNK. | Business-model input only; do not copy the pricing (§19 of the delta). |

---

## 1. Executive summary

- **What PetroApp is (combined evidence):**
  - an operating, two-sided network;
  - fleets pay from a balance;
  - stations record each fueling with photos and attendant attribution;
  - PetroApp pays stations by frequent bank transfer, and a small per-liter fee appears on every record;
  - reporting and exports are mature for the station's needs.
- **What remains unseen:** how vehicles are identified and controlled at the pump (fleet side, chips, limits, budgets, cooldown, offline) and the legal invoicing model.
- **What this means for ZAWED:**
  - The core plan is **validated**: centrally controlled prices, Ops-managed tags, B2B prepaid balance, agent attribution.
  - PetroApp exposes real **gaps in our plan**:
    - a station financial account and payouts;
    - an invoicing domain;
    - evidence capture;
    - station-side self-service boundaries and MFA;
    - asynchronous exports.
  - ZAWED's integrity-at-the-pump design remains a **potential** differentiator that we cannot yet confirm.

## 2. Methodology and access limitations

| Source | Coverage | Limits |
|---|---|---|
| ZAWED baseline | Approved decisions (project memory, demo decision logs), `docs/demo/*`, `docs/architecture/*`, PRD v6 (23 pages), prototype source `apps/web` | Precedence: latest approved decisions > roadmap/architecture docs > PRD v6 > prototype |
| Public research | PetroApp Egypt and Saudi sites, FAQs, 2023 Egypt article, case studies, App Store, Google Play metadata, funding press | Marketing claims; Saudi features may not apply to Egypt |
| Authenticated observation | Station Panel: dashboard, branches, workers, worker log, ledger, sales, rejected sales, service-fee report, branch sales report, groups, users, profile, bank account, tickets, vehicle-shift upload page | **One station, one branch.** No fleet, driver, POS, or admin view. Two pages returned 419. Data window of 3 months. No actions executed. |

**Modules inspected (authenticated): 16 pages or areas.**

1. Dashboard
2. Branches (list, detail, add form)
3. Workers (list, detail)
4. Worker log
5. Ledger
6. Sales
7. Rejected sales
8. Service-fee report
9. Branch sales report
10. Groups
11. Users
12. Profile (account, documents, address, 2FA)
13. Bank account
14. Tickets
15. Vehicle shifts
16. Monthly invoices (419)

## 3. ZAWED baseline (reconstructed)

**Status legend:**

| Status | Meaning |
|---|---|
| **IMPLEMENTED** | Works in the investor prototype on mock data. No capability is production-implemented yet. |
| **APPROVED / PLANNED** | In the approved direction or PRD Phase 1 |
| **LATER PHASE** | PRD Phases 2–4 |
| **PARTIAL** | A screen or model exists, but its actions are not connected |
| **NOT PLANNED** | — |
| **UNRESOLVED** | An open decision |

| Capability | Current status | Source | Phase | Notes |
|---|---|---|---|---|
| Company portal | IMPLEMENTED (home, transactions, vehicles, wallet, budgets, limits, alerts); PARTIAL (reports, requests) | Prototype; PRD §6.2 | P1 | **Approved:** core data read-only; changes by request |
| Station portal | IMPLEMENTED (home, transactions, devices view-only, shifts, settlement cycle) | Prototype; PRD §3 | P1 (view); close shift / payout request P2 | Station Manager scope is narrow in the PRD |
| Operations portal | IMPLEMENTED (home, transactions, alerts, investigation, sync monitor, audit); PARTIAL (companies, top-ups, stations, devices, settlements) | Prototype; PRD §6.3 | P1 | — |
| Super Admin | APPROVED / PLANNED | PRD §6.4 | P1 | The demo merges it into Ops [demo decision] |
| POS | IMPLEMENTED (simulated); hardware/framework UNRESOLVED | Prototype; PRD §6.1; hardware spike | P1 | Kotlin preferred, pending the spike |
| Wallet (company) | IMPLEMENTED | Prototype; PRD §4 | P1 | **Approved:** prepaid only; top-up by bank transfer + Ops credit |
| Immutable ledger | IMPLEMENTED (mock); APPROVED (double-entry internally) | PRD §4.2, §8; architecture decision | P1 | No VIP postings |
| Transactions | IMPLEMENTED | Prototype; PRD §8.3 | P1 | Statuses: completed / declined / pending sync / reversed |
| Budgets | IMPLEMENTED (department, monitor only, never block) | **Approved decision** (not in the PRD) | — | — |
| Limits | IMPLEMENTED (per vehicle, day/week/month, liters/EGP) | PRD §4.1 | P1; dynamic engine P2 | Time-of-day rules are P2 |
| RFID/NFC | PARTIAL (simulated); tag model UNRESOLVED | PRD §7.3, §11; hardware spike | P0–P1 | — |
| Offline operation | IMPLEMENTED (demo) | PRD §7 | P1 | Micro-limit hardening P3 |
| Sync | IMPLEMENTED (auto-sync, conflicts committed + alerted) | PRD §7.5 | P1 | Auto-freeze not implemented |
| Idempotency | IMPLEMENTED (`clientTxnId`) | PRD §9 | P1 | — |
| Cooldown | IMPLEMENTED (tag memory simulated) | PRD §6.1, §7.3 | P1 | Real tag write pending the spike |
| Receipts | IMPLEMENTED (58 mm, `receiptLocale`, QR) | Decisions; PRD §6.1 | P1 | — |
| Printer fail-safe | IMPLEMENTED (digital receipt + QR) | PRD §6.1 | P1 | E-mail / in-app delivery planned |
| Device management | PARTIAL (device list, status, printer, last sync) | PRD §6.3 | P1 | Activation, binding, revocation planned |
| Station management | PARTIAL (Ops list) | PRD §6.3 (Ops-only onboarding) | P1 | Stations owned by a station operator [approved] |
| Worker (agent) management | PARTIAL (agents seeded; shifts view) | PRD §6.1 (ID + PIN set by Ops, lockout, single session) | P1 | No station-side lifecycle or transfer history: **NOT PLANNED** |
| Alerts | IMPLEMENTED (10 types) | PRD §6.3, §8.3 | P1–P3 | — |
| Investigation | IMPLEMENTED (timeline, suspend tag) | Demo decision | — | Timeline illustrative in the demo |
| Fraud controls | IMPLEMENTED (cooldown, limits, tag status, tank-capacity flag); plate OCR APPROVED (P1); visual matching, OTP LATER (P2); geofence LATER (P3) | PRD §10 | P1–P3 | "Block on hard rules, flag soft signals" |
| Reports | PARTIAL (dashboards; reports placeholder) | PRD §6.2 (date-range export by tier, vehicle, station) | P1 | Station reports unspecified |
| Settlements | PARTIAL (station cycle view) | PRD §6.3, §8.3 `Station_Settlements` | P1 | Period summary + "Mark as Paid"; no station ledger |
| Driver app | LATER PHASE | PRD §6.5 | P2 | — |
| Driver identity per transaction | NOT PLANNED | — | — | — |
| Odometer | LATER PHASE (capture after pump capture; health score only; never fraud/billing) | PRD §6.1, Phase 3 | P3 | — |
| Mileage / km per liter | NOT PLANNED (only implied by the health score; telematics P4) | PRD Phase 3–4 | — | — |
| Invoicing (commercial documents) | NOT PLANNED | — | — | Receipts and ledger only |
| E-invoicing (tax) | UNRESOLVED | — | — | Legal entity for wallet funds is also open (PRD Phase 0) |
| ERP | LATER PHASE | PRD Phase 4 | P4 | SAP / Odoo / Dynamics |
| Additional fleet services | LATER PHASE | PRD Phase 4 | P4 | Tolls, wash, maintenance booking |
| Roles / permissions | APPROVED / PLANNED (RBAC matrix) | PRD §3, §10.2 | P0–P1 | Accountant activation in P2 |
| Audit | PARTIAL (Ops audit page) | PRD §6.4 append-only; company audit log P2 | P1–P2 | — |
| Support | PARTIAL (company request types) | PRD §6.2 tickets (company only) | P1 | No station tickets |
| Notifications | PARTIAL (in-app alerts, toasts) | PRD §4.6–4.7 (push/e-mail) | P1–P2 | — |
| Fuel pricing | IMPLEMENTED (per-station price); stale-price flag APPROVED; propagation service LATER (P2) | PRD §6.1, Phase 2 | P1–P2 | — |
| MFA | APPROVED for Ops and Super Admin; Fleet Manager P2; **Station Manager not planned** | PRD §10.2 | — | — |
| Reversals | APPROVED (two-person, credit note) | PRD §6.4 | P1 | — |
| Disputes / adjustments | NOT PLANNED | — | — | — |
| Revenue model; VIP payer; offline negative balances | UNRESOLVED | Demo decisions (open questions) | — | — |

## 4. PetroApp observed product map (Station Panel)

```
Station Panel (Arabic RTL; light/dark)
├─ Dashboard: pending/rejected invoice counters, active/inactive workers,
│   annual and monthly consumption by fuel type (charts → CSV/PNG/SVG)
├─ Groups & users: groups (permission list shows only "All"), company users (auto credentials)
├─ Branches: list/search, view (names, address, branch login, map),
│   add/edit/delete [ACT], deleted-records archive
├─ Workers: list (connected badge), view (national ID, employee no.),
│   edit / change branch / deactivate [ACT]; no "add"
├─ Worker log: transfers between branches (by whom, when)
├─ Statement of account (ledger): opening balance, daily invoice totals,
│   daily service-fee totals, bank transfers in, running balance; Excel [ACT]
├─ My sales: invoices (13 columns incl. pump & odometer photos; 8 filters;
│   totals bar; Excel / e-mail export [ACT]); rejected invoices
├─ Monthly aggregated invoices → 419
├─ Service-fee report (daily; print invoices / Excel [ACT])
├─ Branch sales report (fuel type × cost/quantity per branch; Excel [ACT])
├─ Vehicles & drivers → Shifts (Excel template + upload under /company/) [ACT]
├─ Tickets (categories incl. chip & installation; add → 419) + WhatsApp support
└─ Profile: account (tax no., CR no.), documents, national address (Saudi "Wasl"),
    password, optional 2FA ("…or link a new device"); bank account form
```

**Key quantitative observations** (one station, 1 Sep–15 Oct 2026, aggregate only):

| Metric | Value |
|---|---|
| Fueling records | 1,636, all **company sales paid from balance** (0 cash, 0 individual) |
| Average fueling | ≈ 61 L, ≈ 1,252 EGP |
| Fuel mix | Diesel ≈ 90% of 2026 value |
| Service fee | **0.05 EGP/L** ≈ 3 EGP per fueling ≈ **0.24% of value** |
| Payouts | 22 bank transfers in September (35k–155k EGP each), i.e. near-daily |
| Prices | Uniform per fuel type across all records |

## 5. PetroApp capabilities, reconciled

Each claim is shown with its strongest current evidence.

| # | Capability | Strongest evidence | Notes |
|---|---|---|---|
| P1 | NFC smart chip per vehicle | PUB-EG + NOT-OBS-STN | Ticket category "chip & installation" [INF] |
| P2 | QR / smart-card identification | PUB-SA + NOT-OBS-STN | — |
| P3 | Fleet web control panel | PUB-EG + N/A-STN | — |
| P4 | Mobile app for supervisors and drivers | PUB-EG/SA | — |
| P5 | Company wallet / balance | AUTH-OBS (indirect: payment method "from balance") + PUB-EG | Wallet screens N/A-STN |
| P6 | Budget distribution | PUB-EG + N/A-STN | Block vs monitor UNK |
| P7 | Per-vehicle limits (daily, monthly, per trip) | PUB-EG + N/A-STN | — |
| P8 | Pre-fueling permission check | PUB-SA + N/A-STN | — |
| P9 | Over-consumption alerts | PUB-EG + NOT-OBS-STN | Notifications bell showed nothing |
| P10 | Per-vehicle reports | PUB-EG + NOT-OBS-STN | Station has branch and fuel-type reports instead |
| P11 | Station reporting: consumption charts, branch sales matrix, service-fee report, sales totals | **AUTH-OBS** | — |
| P12 | Exports: Excel; e-mail for large exports; chart CSV/PNG/SVG; print invoices | **AUTH-ACT** | — |
| P13 | Per-fueling "invoice" records with photos | **AUTH-OBS** | — |
| P14 | Monthly aggregated invoices | **N/A-STN** (419) | — |
| P15 | Certified e-invoices / VAT | PUB-EG; tax fields AUTH-OBS; e-invoice status NOT-OBS-STN | — |
| P16 | Invoice pending/rejected states | AUTH-OBS (labels, counters, empty list) | Workflow UNK |
| P17 | Station ledger with payouts | **AUTH-OBS** | Balance meaning INF |
| P18 | Station bank account + bank letter | AUTH-OBS / AUTH-ACT | Verification UNK |
| P19 | Service fee 0.05 EGP/L | **AUTH-OBS** (computed from displayed values) | Payer UNK |
| P20 | Pump photo per fueling | **AUTH-OBS** | — |
| P21 | Odometer photo per fueling | **AUTH-OBS** | Numeric value NOT-OBS-STN |
| P22 | Odometer / distance tracking | PUB-SA | — |
| P23 | Attendant (worker) attribution per fueling | **AUTH-OBS** | — |
| P24 | Driver identity ("Drive" code) | PUB-SA + NOT-OBS-STN | — |
| P25 | Worker lifecycle (status, transfer + log, deactivate) | AUTH-OBS / AUTH-ACT | No add from the station side |
| P26 | Branch self-service (add/edit/delete, login, fuel types, amenities, map) | **AUTH-ACT** | PetroApp approval UNK |
| P27 | Groups and users | AUTH-OBS (forms) | Permissions: "All" only |
| P28 | Optional 2FA; device linking | **AUTH-OBS** | — |
| P29 | Support tickets + WhatsApp | AUTH-OBS; add ticket N/A-STN (419) | — |
| P30 | Vehicle shifts via Excel | **AUTH-ACT** | Meaning INF (time-window rule?) |
| P31 | Fuel price control | NOT-OBS-STN (no price fields; uniform prices) | Central / regulated [INF] |
| P32 | Station network (600+ in Egypt) | PUB-EG | — |
| P33 | Vehicle services (wash, oil, tires) | PUB-SA; Egypt station: **amenity tags only** (AUTH-OBS) | — |
| P34 | ERP integration ("any ERP") | PUB-EG | — |
| P35 | Cash and individual (retail) payment modes | AUTH-OBS (filters; no data) + PUB (retail plans) | — |
| P36 | Offline / sync | NOT-OBS-STN | UNK |
| P37 | Cooldown, tank-capacity check, geofence | NOT-OBS-STN | UNK |
| P38 | Device / POS management | NOT-OBS-STN | Device linking in the 2FA text only |
| P39 | Per-vehicle SaaS pricing tiers + chip + installation | PUB-EG | — |
| P40 | Fleet App add-ons (weight sensors, regulators) | PUB-SA | Saudi-specific |

**Total: 40 capabilities.**

Counted by the strongest evidence for each capability:

| Evidence | Count |
|---|---|
| Authenticated (observed or visible action) | 19 |
| Public only | 16 |
| Not observed, not accessible or inferred only | 5 |

## 6. Master feature matrix

| Capability | ZAWED status | PetroApp evidence | PetroApp capability | Gap type | Stronger | Confidence | Action | Priority |
|---|---|---|---|---|---|---|---|---|
| Cashless fleet fueling from a prepaid balance | IMPLEMENTED (demo) / PLANNED | AUTH-OBS | All observed sales "from balance" | NO GAP | Even | High | KEEP | P0 |
| Vehicle identification at the pump | PARTIAL (simulated NFC) | PUB + NOT-OBS-STN | NFC chip claimed | UNKNOWN | UNK | Low | RESEARCH MORE (hardware spike) | P0 |
| Attendant attribution | IMPLEMENTED | AUTH-OBS | Worker per invoice | NO GAP | Even | High | KEEP | — |
| Driver identity | NOT PLANNED | PUB-SA + NOT-OBS-STN | Driver code (Saudi) | UNKNOWN | UNK | Low | RESEARCH MORE | P2 |
| Pump photo evidence | LATER (visual matching P2) | AUTH-OBS | Photo per fueling | COMPETITIVE GAP | PetroApp | High | ADAPT (conditional evidence policy) | P1 |
| Odometer | LATER (P3) | AUTH-OBS (photo) | Photo per fueling | ROADMAP GAP | PetroApp | High (photo), Low (numeric) | MOVE EARLIER + ADAPT (numeric + optional photo) | P1 |
| Fuel pricing control | IMPLEMENTED (Ops sets per station) | NOT-OBS-STN | Not station-controlled | NO GAP | Even | Medium | KEEP; ADAPT: national price table with effective dates | P1 |
| Price propagation to devices | LATER (P2) | INF | Uniform prices | ROADMAP GAP | UNK | Medium | MOVE EARLIER (pilot) | P0 |
| Company wallet + immutable ledger | IMPLEMENTED / PLANNED | N/A-STN | UNK | NOT COMPARABLE | UNK | — | BUILD AS PLANNED | P0 |
| **Station financial account / statement** | PARTIAL (period summary) | AUTH-OBS | Daily-netted ledger + payouts | COMPETITIVE GAP | PetroApp | High | ADD | P0 (pilot) |
| Station payouts (bank) | PLANNED ("Mark as Paid") | AUTH-OBS | Near-daily transfers | IMPLEMENTATION GAP | PetroApp | High | ADAPT (payout records + bank reference reconciliation) | P0 (pilot) |
| Invoicing domain (fuel + fee documents) | NOT PLANNED | AUTH-OBS / N/A-STN / PUB | Per-fueling records, monthly aggregation, fee invoices | ROADMAP GAP | PetroApp | High | ADD (after legal research) | P0 |
| E-invoice compliance | UNRESOLVED | PUB only | Claimed "certified" | ROADMAP GAP | PetroApp (claimed) | Medium | RESEARCH MORE before the pilot | P0 |
| Transaction finality (no post-hoc rejection) | IMPLEMENTED / PLANNED (reversal) | AUTH-OBS (pending/rejected states) | Review states exist | DIFFERENT APPROACH | ZAWED (potential) | Medium | KEEP + ADD disputes/adjustments | P1 |
| Transaction detail / audit trail | IMPLEMENTED | AUTH-OBS (no detail page) | List only | DIFFERENT APPROACH | ZAWED (station view) | Medium | KEEP | — |
| History window | PLANNED (7-year retention) | AUTH-OBS | 3 months visible | DIFFERENT APPROACH | ZAWED (design) | Medium | KEEP | — |
| Reports: station | PARTIAL (dashboard) | AUTH-OBS | Sales totals, branch × fuel matrix, fee report, charts | IMPLEMENTATION GAP | PetroApp | High | ADD to PRD scope (station report set) | P1 |
| Reports: company / per vehicle | PARTIAL / PLANNED | PUB + N/A-STN | Claimed | IMPLEMENTATION GAP | UNK | Low | BUILD AS PLANNED | P1 |
| Exports | PLANNED (date-range export) | AUTH-ACT | Excel, e-mail for large, chart CSV/PNG/SVG | IMPLEMENTATION GAP | PetroApp | High | ADAPT (XLSX/CSV/PDF + asynchronous jobs; skip chart images) | P1 |
| Limits | IMPLEMENTED / PLANNED | PUB + N/A-STN | Claimed (incl. per trip) | UNKNOWN | UNK | Low | BUILD AS PLANNED | P1 |
| Time windows / vehicle shifts | LATER (P2 allowed days/hours) | AUTH-ACT (Excel upload) | Shifts upload | DIFFERENT APPROACH | UNK | Low | ADAPT (schedule in the policy engine; CSV bulk import as a convenience) | P2 |
| Budgets | IMPLEMENTED (monitor only) | PUB + N/A-STN | Claimed | UNKNOWN | UNK | Low | KEEP | — |
| Cooldown (tag level) | IMPLEMENTED (sim) / PLANNED | NOT-OBS-STN | UNK | UNKNOWN | UNK | — | KEEP | P0 |
| Offline + sync | IMPLEMENTED (demo) / PLANNED | NOT-OBS-STN | No indicators | UNKNOWN | ZAWED (potential) | Low | KEEP | P0 |
| Tank-capacity anomaly | IMPLEMENTED (demo) / LATER (P3) | NOT-OBS-STN | UNK | UNKNOWN | UNK | — | KEEP | P2 |
| Alerts + investigation | IMPLEMENTED | NOT-OBS-STN | No alert module for stations | NOT COMPARABLE | UNK | Low | KEEP | P1 |
| Tag lifecycle | PLANNED (Ops) | NOT-OBS-STN + INF (tickets) | Via support | NO GAP | Even | Low | KEEP | P1 |
| Device management / health | PARTIAL / PLANNED | NOT-OBS-STN | Device linking only | DIFFERENT APPROACH | ZAWED (potential) | Low | KEEP | P1 |
| Station self-service (branches) | NOT PLANNED (Ops-only onboarding) | AUTH-ACT | Add/edit/delete branches + logins | DIFFERENT APPROACH | PetroApp (convenience) | High | ADAPT (tiered boundary) | P1 |
| Worker lifecycle | PARTIAL / NOT PLANNED (station side) | AUTH-OBS/ACT | Transfer + log + deactivate | COMPETITIVE GAP | PetroApp | High | ADAPT | P1 |
| Station roles / users | PLANNED (Station Manager only) | AUTH-OBS | Groups (All), users, branch logins | ROADMAP GAP | PetroApp (breadth) | Medium | ADAPT (fixed roles, not "All") | P2 |
| Station MFA | NOT PLANNED | AUTH-OBS (optional 2FA) | Optional 2FA | COMPETITIVE GAP | PetroApp | High | ADD (required for financial actions) | P1 |
| Station support tickets | NOT PLANNED | AUTH-OBS | Tickets + WhatsApp | ROADMAP GAP | PetroApp | High | ADD | P2 |
| Company self-service | APPROVED read-only + requests | PUB + N/A-STN | Claimed | UNKNOWN | UNK | Low | KEEP (revisit after the pilot) | P2 |
| Driver app | LATER (P2) | PUB | Station map etc. | ROADMAP GAP | PetroApp | Medium | BUILD AS PLANNED (later) | P2 |
| Station amenities metadata | NOT PLANNED | AUTH-OBS | Amenity tags | ROADMAP GAP | PetroApp | High | ADD later (with the station locator) | P3 |
| Vehicle services commerce | LATER (P4) | PUB-SA only | Saudi only | NOT COMPARABLE | PetroApp (Saudi) | Medium | MOVE LATER / keep P4 | P3 |
| ERP | LATER (P4) | PUB | Claimed | ROADMAP GAP | UNK | Low | ADAPT (exports + read API first) | P2 |
| Cash / individual payments | NOT PLANNED | AUTH-OBS filters | Supported modes | DIFFERENT APPROACH | — | High | DO NOT COPY | — |
| Bilingual UX | IMPLEMENTED | AUTH-OBS (Arabic only seen) | Selector exists | UNKNOWN | UNK | Low | KEEP | — |
| Light / dark | IMPLEMENTED | AUTH-OBS | Toggle | NO GAP | Even | High | KEEP | — |

## 7. PetroApp advantages

### Product capability

| Advantage | Evidence |
|---|---|
| Pump and odometer photo evidence | AUTH |
| A live station ledger with payouts | AUTH |
| Station reports and exports, including e-mail delivery of large exports | AUTH |
| Worker transfer audit | AUTH |
| Branch self-service | AUTH |
| Optional 2FA for stations | AUTH |
| Station tickets | AUTH |
| Tax/commercial profile and invoice documents | AUTH / PUB |
| Driver app and ERP claims | PUB |

### Market and operational maturity

- a live 600+ station network in Egypt;
- real fleet volume;
- near-daily payout operations;
- chip installation logistics;
- case studies;
- funding.

None of these is a product feature that ZAWED can match by building screens.

## 8. ZAWED advantages: differentiator classification

| Capability | Classification | Evidence basis |
|---|---|---|
| Offline-first POS | POTENTIAL | ZAWED designed + demonstrated; PetroApp station view shows no offline/sync state; fleet/POS unseen |
| Offline queue | POTENTIAL | Same |
| Synchronization visibility (origin vs sync time) | POTENTIAL | No sync fields in the PetroApp station view |
| Idempotency | INSUFFICIENT EVIDENCE | Sequential invoice numbers seen; duplicates untestable |
| Duplicate financial protection | INSUFFICIENT EVIDENCE | — |
| Cross-device cooldown | INSUFFICIENT EVIDENCE | — |
| RFID tag-level controls | INSUFFICIENT EVIDENCE | PetroApp chips managed via support |
| Printer fail-safe | NOT COMPARABLE | PetroApp attendant device appears to be a phone app; receipts unseen |
| Immutable company ledger | INSUFFICIENT EVIDENCE | Company side unseen; station ledger is SIMILAR in concept |
| Explicit transaction audit (detail + timeline) | POTENTIAL | No invoice detail page in the station view |
| Tank-capacity anomaly detection | INSUFFICIENT EVIDENCE | — |
| Investigation timeline | POTENTIAL | No alert/investigation module visible to stations; admin unseen |
| Tag suspension | INSUFFICIENT EVIDENCE | — |
| Device health (station-visible) | POTENTIAL | Not in the PetroApp station panel |
| Sync health (station-visible) | POTENTIAL | Same |
| Tenant isolation | NOT COMPARABLE | Untestable |
| Bilingual UX | INSUFFICIENT EVIDENCE | Arabic only observed |
| Responsive portals | INSUFFICIENT EVIDENCE | Mobile not tested |
| Live dashboard updates | INSUFFICIENT EVIDENCE | — |
| Budget vs hard-limit separation | INSUFFICIENT EVIDENCE | Fleet side unseen |
| Transaction finality (no post-fueling rejection) | POTENTIAL | PetroApp shows pending/rejected states; workflow UNK |
| Long accessible history | POTENTIAL | PetroApp station view: 3 months |
| Evidence photos | **PETROAPP STRONGER** | Observed vs planned |
| Station payouts | **PETROAPP STRONGER** | Observed operation |
| Station 2FA | **PETROAPP STRONGER** (vs our PRD) | Observed |

**No capability qualifies as CONFIRMED.**

## 9. Missing features (updated)

| Feature | Classification | Priority | Change from v0.1 |
|---|---|---|---|
| Invoicing domain + e-invoice compliance | ADAPT | P0 | Strengthened; research the legal model before the pilot |
| Station financial account + payouts | ADAPT | P0 | **New** |
| Price table with effective dates + propagation | ADAPT / move earlier | P0 | **New** |
| Conditional fueling evidence (pump photo) | ADAPT | P1 | **New** |
| Odometer (numeric + optional photo) | ADAPT / move earlier | P1 | Strengthened |
| Station report set + asynchronous exports | ADAPT | P1 | Strengthened |
| Agent lifecycle (suspend, transfer, history) | ADAPT | P1 | **New** |
| Station MFA | COPY | P1 | **New** |
| Station self-service boundary | ADAPT | P1 | **New** |
| Disputes and adjustments (no rejection state) | ADAPT | P1 | **New** |
| Station support tickets | COPY | P2 | **New** |
| Driver identity | LATER / research | P2 | Weakened (was P1) |
| Company self-service | LATER / research | P2 | Weakened (was P1) |
| Per-trip allowances | LATER | P2 | Unchanged (public only) |
| Fueling schedules (vehicle shifts) | ADAPT | P2 | Already PRD P2; model cleanly |
| Station amenities | LATER | P3 | **New** |
| Vehicle services, consumer app, Saudi add-ons | SKIP / LATER | P3 | Unchanged |

## 10. Features we should NOT copy

1. Post-completion "rejected" or "pending" fueling states. Use reversals, adjustments and disputes.
2. Self-service bank-account edits without verification and dual control.
3. Branch creation without approval, and unrestricted branch logins.
4. Excel upload as the main configuration path.
5. Permission UIs with only "All".
6. Menu items that lead to 419 pages.
7. Imported foreign-market modules (Saudi national address, municipality licence).
8. Cash and individual (retail) modes inside the B2B product.
9. A 3-month visible history limit.
10. Chart-image exports as a feature goal.
11. Storing national ID numbers without a legal need.

## 11. UX comparison: things ZAWED should deliberately do better

| PetroApp observation | ZAWED intent |
|---|---|
| 419 errors on menu items | Role-aware navigation: never show what a role cannot open |
| "All"-only permissions | Fixed, named roles from the RBAC matrix; no free-form permission sets in V1 |
| No invoice detail page | Every transaction opens to a detail view with its evidence, ledger effect and timeline |
| 3-month history | Full retained history with fast date filters; asynchronous export for large ranges |
| Misleading group ("Vehicles & drivers" → only Shifts) | Navigation labels match their contents |
| Mixed-language labels; Saudi-specific forms in Egypt | Full AR/EN parity (already enforced by tests); Egypt-specific data model |
| Excel-heavy workflows | In-product forms and rules first; CSV import only for bulk |
| Bell with no feedback | Notifications always lead somewhere actionable |
| Two "Apply" clicks for date filters | One-step filters (already the prototype pattern) |
| Daily-aggregated ledger only | Statement with drill-down to each transaction and payout |

**Where PetroApp's UX is good:**

- the totals bar on the sales list;
- consistent empty states;
- searchable branch switcher;
- e-mail delivery for large exports;
- light/dark mode.

## 12. Fraud and control comparison

| Area | PetroApp | ZAWED |
|---|---|---|
| Station-visible controls | Evidence capture (photos), attendant attribution, plate + VIN, after-the-fact invoice review, worker audit, optional 2FA [AUTH] | — |
| Prevention controls | Claimed only [PUB] | Prevention at the pump (cooldown first, tag status, limits, balance) plus non-blocking anomaly flags and investigation |

These are complementary approaches. Adding conditional evidence would combine prevention with proof.

## 13. Offline capability comparison

**Unchanged:** unproven for PetroApp. The station view shows **no** offline or sync indicators [NOT-OBS-STN]. ZAWED surfaces origin and sync times to company, station and Ops.

## 14. Financial control comparison

| Area | PetroApp | ZAWED |
|---|---|---|
| Company wallet | "Paid from balance" observed; wallet screens unseen | Ledger-backed prepaid wallet (planned) |
| Station side | Running ledger + frequent payouts **observed** | Period settlement summary only (planned) |
| Commercial documents | Per-fueling invoices, monthly aggregation and fee invoices exist | None planned |

**Verdict:** PetroApp is operationally ahead on the station and document side. ZAWED's company-side ledger design cannot be compared.

## 15. Driver experience comparison

No new authenticated evidence; the driver app is PUBLIC only. Driver identity is not visible to stations. ZAWED's Phase 2 plan is unchanged.

## 16. Station operations comparison

| PetroApp station capability [AUTH] | ZAWED plan |
|---|---|
| Branches with their own logins | Stations owned by an operator; Ops-only onboarding |
| Worker lifecycle with transfer log | Agents created and managed by Ops |
| Sales with photos and totals | Station transactions view (no evidence) |
| Ledger and payouts | Settlement summary + "Mark as Paid" |
| Branch and fee reports | Station reports unspecified |
| Tickets | No station tickets |

**Not visible in PetroApp's station panel:**

- devices;
- shifts (open/close);
- price control;
- alerts.

ZAWED plans devices and shifts for stations. Shift closing and reconciliation are P2.

## 17. Reporting and analytics comparison

PetroApp's station reporting is mature and simple:

- sales list with totals;
- branch × fuel-type matrix;
- fee report;
- yearly and monthly charts;
- Excel export everywhere;
- e-mail export for large datasets.

**Recommendation for ZAWED:**

- **Station report set:** sales by period, fuel type and branch; statement; payouts.
- **Company report set:** per vehicle, per department, wallet statement, monthly pack.
- **Formats:** XLSX/CSV/PDF through asynchronous export jobs with in-app download links.

## 18. Integrations comparison

No new authenticated evidence. Keep "exports + read API first, ERP connectors on demand."

## 19. New product ideas (refined by the evidence)

1. **Evidence policy engine:** evidence capture is required only when risk rises; a small random sample builds trust (§A of the delta).
2. **Odometer → km/L:** a numeric reading with optional photo, monotonic checks, and efficiency analytics for fleet managers.
3. **Station statement with drill-down:** sales → fees → payouts → bank reference, reconciled.
4. **Payout predictability for stations:** a payout schedule visible to the station (e.g. T+1), with status for each payout.
5. **Effective-dated national price table:** a price change is scheduled once and propagates to every device before its effective time.
6. **Fueling schedules:** time windows per vehicle group in the policy engine, instead of shift spreadsheets.
7. **Dispute cases:** structured, evidence-linked, resolved only by compensating entries.
8. **Station amenities:** feed the future driver station locator.

## 20. Recommended priorities

Full detail is in the delta document.

| Priority | Features |
|---|---|
| **P0 (pilot)** | Station financial account + payout records; price table with effective dates + propagation; invoicing legal decision + minimum documents; NFC/tag decision (spike) |
| **P1 (V1)** | Conditional evidence capture; odometer (numeric + optional photo); station and company report sets + asynchronous exports; agent lifecycle; station MFA; station self-service boundary; disputes/adjustments; full invoicing module |
| **P2** | Driver identity (research); fueling schedules; station tickets; station roles; company self-service review; ERP read API |
| **P3** | Amenities; services; consumer features |

## 21. Investor differentiation

These framings are validated against the evidence. The supporting rows show what can and cannot be said.

| # | Message (safe framing) | Evidence status |
|---|---|---|
| 1 | "ZAWED is designed for transaction integrity at the pump: every check runs before fuel is dispensed." | ZAWED: designed + demo. PetroApp: unknown. Do not compare. |
| 2 | "Fueling works without network, and every party sees what is pending and when it synced." | ZAWED demo. PetroApp station view shows no sync state. Say "explicit sync visibility," not "PetroApp can't." |
| 3 | "A completed fueling is final; corrections are auditable reversals, never silent rejections." | ZAWED design. PetroApp shows rejection states. Use only as a ZAWED principle. |
| 4 | "Every transaction is investigable end to end: detail, timeline, action." | ZAWED demo. |
| 5 | "Stations, fleets and operations share one live system." | ZAWED demo. |
| 6 | "Bilingual, mobile, light and dark, built for Egyptian operators." | ZAWED demo. Neutral claim. |

## 22. Proposed roadmap

See [zawed-roadmap-delta.md](zawed-roadmap-delta.md). It supersedes the v0.1 roadmap section.

## 23. Open questions

The prioritised research backlog is in the delta document (§14). The main open items:

- the fleet portal;
- the identification mechanism;
- limits, budgets, cooldown, offline;
- the rejection workflow;
- monthly invoices and tax;
- the settlement contract;
- price updates.

## 24. Final recommendations

1. **Keep ZAWED's core plan.** Prices, tags, prepaid balance, finality and offline are validated or unchallenged.
2. **Add the station-side financial plane:** station account, payouts and statements. Make it pilot-blocking.
3. **Add invoicing as a domain** after a legal decision. That decision blocks the pilot.
4. **Adapt evidence capture and odometer** as conditional, configurable features in V1.
5. **Define station self-service boundaries, MFA and agent lifecycle** before onboarding real stations.
6. **Keep the investor demo frozen.** Use the safe framings above.

---

### Sources

- **Authenticated:** `petroapp-authenticated-product-observation.md` (owner-supplied, 15 Oct 2026, not in the repository).
- **Public:**
  - https://petroapp.com/egypt/
  - https://petroapp.com/egypt/faqs/
  - https://petroapp.com/egypt/ar/companies-ar/
  - https://petroapp.com/egypt/2023/06/07/petroapp-innovation-solution-for-fleet-fuel-management/
  - https://petroapp.com/egypt/case-studies/
  - https://petroapp.com/en/
  - https://apps.apple.com/app/id6447249025
  - https://apptopia.com/google-play/app/petro.petroapp/about
  - https://www.wamda.com/index.php/2025/06/jadwa-leads-50-million-investment-saudi-fueltech-petroapp
  - https://cairoscene.com/Business/Saudi-Fintech-PetroApp-Secures-50M-to-Grow-Fuel-Fleet-Services
  - https://enterpriseam.com/logistics/2025/07/01/jadwa-backs-petroapp-with-usd-45-mn-ahead-of-ipo/
