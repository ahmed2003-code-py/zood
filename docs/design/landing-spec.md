# Landing page and "Request a demo" (U1)

The public page at `/`, built on 2026-11-20 from the owner's brief in `docs/handover/HANDOVER.md` §4.
Code: `apps/web/src/landing/`. Copy: `apps/web/src/i18n/locales/{ar,en}/landing.json`.

## Routes

| Route | What | Notes |
|---|---|---|
| `/` | Landing page | Both API modes. Lazy route, so the portals' entry chunk does not carry it. |
| `/login` | Sign-in | Linked from the header ("دخول" / "Sign in") and the footer. |
| `/demo` | Investor demo launcher | Unchanged. Linked quietly from the footer. |
| `/ops/leads` | Ops "Demo requests" | Nav entry under Customers, both modes. Ops portal = platform roles only. |

## Sections, in order

1. **Header** (fixed). Transparent over the hero with light text; a solid bar in the page theme once the page scrolls. Section links (desktop) or a menu (phone), language switch, theme switch, Sign in, Request a demo.
2. **Hero.** Headline, lead, two actions, three assurances, and a live POS fill-up drawn in DOM/CSS: tag read → vehicle verified → litres count up → balanced ledger entry (wallet −, station payable +) → receipt prints. Labelled "Illustration"; the same story is written as three step chips and a screen-reader sentence.
3. **The problem.** A comparison table, "Today" against "With ZAWED", for fuel cards, cash and advances, paper invoices, and fraud. Stacks on phones with visible column labels.
4. **How it works.** Three numbered steps (top up → fuel with the tag → every litre recorded), joined by a line that draws on reveal.
5. **For fleets.** Six features and a sample vehicle card (plate, monthly limit, cooldown, fuel grade).
6. **For stations.** Six features and a sample payable card (fill-ups adding to the payable, a payout awaiting its second approval). Competitors have nothing for stations.
7. **Trust and safety.** Eight product-true claims in a spec-sheet grid.
8. **Made in Egypt.** EGP to the piastre, Cairo time (with a live Cairo clock), Arabic first, support in Egyptian Arabic, e-invoicing stated honestly as roadmap.
9. **Pricing.** Five tiers by fleet size, monthly/yearly toggle, "indicative" on every price and a notice above them; "Get a quote" pre-selects the fleet size in the form.
10. **FAQ.** Radix accordion: each question is an `h3` with a button.
11. **Request a demo.** Form plus WhatsApp and e-mail.
12. **Footer.** Section links, Sign in, investor demo, contact, language and theme switchers.

## Copy decisions

- Arabic is Egyptian, as people in the trade speak it ("بيفوّل", "الكارت", "عربية", "إزاي"), with Western digits. English is plain and short. Both went through the `humanizer` skill: no "not X but Y", no slogans, no invented authority.
- **No invented numbers.** There is no pilot data, so there are no counts of stations, no savings percentages, no response-time promises. Every claim is something the product does today: per-vehicle limits and cooldowns, tag bound to one vehicle, two-person approval on top-ups, adjustments, payouts and reversals, offline fueling with the same rules and conflict alerts, double-entry ledger with scheduled reconciliation, integer piastres, database-level tenant isolation, append-only audit log.
- **E-invoicing** is described as "monthly statements today, ETA e-invoice connection on the roadmap" (implementation plan lists the e-invoice module as later work).
- Terms: the vehicle's RFID tag is "كارت RFID" / "الكارت" (the word the portals use); "fill-up" in English.
- Sample values in the illustrations (plates, 40.00 L at 19.25 EGP, a 48,215.50 payable) are labelled as illustrations.

## Contact details (temporary, from the owner)

`apps/web/src/landing/contact.ts`: WhatsApp 01002589923 (`https://wa.me/201002589923`), e-mail `zawed-fuel@gmail.com`. A test fails if the misspelt address ever appears.

## Demo form

Fields: company name (2–120), contact name (2–80), Egyptian mobile, e-mail (≤ 254), fleet size (`1-19`, `20-49`, `50-99`, `100-249`, `250+`).

- Validation runs on submit, then live while typing. Errors sit under each field (`aria-invalid`, `aria-describedby`), a summary is announced with `role="alert"`, and focus moves to the first wrong field.
- Phone: spaces, dashes, brackets, `+20` / `0020` / `20`, and Arabic-Indic or Persian digits are accepted and normalised to `01[0125]XXXXXXXX` (shown normalised on blur). Rules in `src/domain/demo-request.ts`, shared with the mock API.
- Spam: a honeypot `website` field (off-screen, `aria-hidden`, `tabIndex=-1`, `autocomplete=off`) sent as found, and `startedAt` (ms, when the form rendered). The server judges both; the mock drops honeypot requests silently.
- States: sending (button spinner), success (focus moves to the confirmation, WhatsApp shortcut, "send another"), rate limited (`429 RATE_LIMITED`, wait in minutes from `retryAfterSeconds` or `Retry-After`), validation (`400 VALIDATION_FAILED`, the blamed fields are marked), network, and anything else (with the request reference).

## API contract (the API agent implements the same)

- `POST /api/v1/public/demo-requests` (no auth) → `202 { "status": "RECEIVED" }`, `400 VALIDATION_FAILED`, `429 RATE_LIMITED`.
- `GET /api/v1/ops/leads?status=&cursor=&limit=` → `{ items: Lead[], nextCursor }`.
- `PATCH /api/v1/ops/leads/{id}` `{ status, note? }` → the lead. The web sends `note` only when it changed; an empty string clears it.

Types in `src/api/types.ts`; HTTP in `src/api/http-api.ts`; mock in `src/mocks/leads.ts` (per browser, 5 requests per 10 minutes, three seeded example leads).

## Motion

| What | How | Timing |
|---|---|---|
| Hero background | Three radial glows (`--hero-glow-1/2/3`) drifting with `transform` only | 22–32 s, alternate, paused off screen |
| Entrance | Headline, lead, actions and assurances rise and unblur in sequence; the terminal rises last | 900–1100 ms, exponential ease-out, 0–600 ms stagger |
| POS fill-up | Phase timeline (≈ 12.4 s loop), litre counter via `requestAnimationFrame` written to text nodes, receipt by `clip-path` | Pauses off screen and in background tabs |
| Scroll reveals | `IntersectionObserver` sets `data-revealed`; opacity + 14 px rise; small per-item stagger; the steps line draws | 700 ms |
| FAQ | Radix accordion height | 200–260 ms |

Everything is in `src/landing/landing.css`, loaded with the landing chunk. Under `prefers-reduced-motion` all of it is off: no reveal hiding, no drift, and the POS shows the finished fill-up with the receipt printed. The global reduced-motion rule in `index.css` now also sets `animation-iteration-count: 1`, so infinite animations (the POS scan rings) stop instead of flickering.

## Tokens added (both themes)

`--hero-foreground`, `--hero-muted` (text on the hero, in the contrast test), `--hero-line` and `--hero-panel` (hairlines and glass panels on the hero), `--hero-glow-1`, `--hero-glow-2`, `--hero-glow-3` (the moving glows: brand greens and the logo orange). Utilities: `text-hero-foreground`, `text-hero-muted`, `border-hero-line`, `bg-hero-panel`.

## Placeholders the owner must replace

| What | Where |
|---|---|
| Tier prices per vehicle per month (150 / 130 / 115 / 100 EGP, custom for 250+) | `src/landing/pricing.ts` (`PLACEHOLDER` comments) |
| Yearly = 10 months | `YEARLY_BILLED_MONTHS` in the same file |
| RFID tag price (250 EGP one-time) | `TAG_PRICE_PER_VEHICLE` in the same file |
| "Indicative" labels | Remove from `Pricing.tsx` and `landing.json` once prices are final (`PRICES_ARE_PLACEHOLDERS`) |
| Contact WhatsApp and e-mail (temporary) | `src/landing/contact.ts` |
| Station joining terms ("agreed with each station") | `pricing.stationsNote` in `landing.json` |

## Performance

The landing route is its own chunk (JS about 87 kB, 26 kB gzip, including both languages' copy and the accordion; CSS 4 kB). The entry chunk did not grow: the demo-request mock and the HTTP client load on first use. No images, video or animation library.
