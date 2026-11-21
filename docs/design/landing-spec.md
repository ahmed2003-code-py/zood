# Landing page and "Request a demo" (U1)

The public page at `/`, built on 2026-11-20 from the owner's brief in `docs/handover/HANDOVER.md` §4.
Code: `apps/web/src/landing/`. Copy: `apps/web/src/i18n/locales/{ar,en}/landing.json`.

## Routes

| Route | What | Notes |
|---|---|---|
| `/` | Landing page | Both API modes. Lazy route, so the portals' entry chunk does not carry it. |
| `/login` | Sign-in | Linked from the header ("دخول" / "Sign in"; in the menu on phones) and the footer. See `sign-in.md`. |
| `/privacy`, `/terms` | Privacy policy and terms of use | Lazy, both languages. Linked from the footer, the sign-in footer and the demo form's consent line. |
| `/demo` | Investor demo launcher | Unchanged. Linked from the footer only in mock mode (`API_MODE === 'mock'`); against the API it has nothing to show. |
| `/ops/leads` | Ops "Demo requests" | Nav entry under Customers, both modes. Ops portal = platform roles only. |

## Sections, in order

1. **Header** (fixed, full width; pass 2). An edge-to-edge bar whose content sits on the page grid (`max-w-6xl`). Over the hero it is a light veil on the stage (`bg-stage-panel`); after 24 px of scroll it firms up into a blurred bar in the page theme. From lg: section links with the speed line under the one in view (`useActiveSection`, `aria-current`), language and theme buttons, Sign in, Request a demo. Below lg: logo, Request a demo and a menu button that opens a full-screen menu (Radix Dialog: focus trapped, Escape closes, page scroll locked through the overlay, focus back on the button; a section link closes the menu and moves focus and scroll to that section). The language button's accessible name is "Language: English" / "اللغة: العربية", so it contains the visible text.
2. **Hero** (stage). Headline, lead, two actions, three assurances, and the **product collage**: one fill-up seen on three real surfaces drawn from the design system (the company portal's wallet balance and latest fill-ups, the station POS, the receipt). The POS reads the tag and counts the litres, the new row arrives in the statement and the balance goes down, the receipt prints. Drawn at 460 × 540 and scaled to its column (`useFitScale`), labelled "Illustration", described in one screen-reader sentence, and told again as three step chips.
3. **The problem.** "Today" against "With ZAWED"; the ZAWED column uses `stage-tint` (a quiet green-white in Light, a neutral lift in Dark).
4. **How it works.** Three numbered steps joined by a line that draws on reveal.
5. **For fleets.** Six features and a sample vehicle card.
6. **For stations.** Six features and a sample payable card.
7. **Security** (`#trust`). The **money path**: bank transfer → (two people approve) → company wallet → (balanced ledger entry at the pump, even offline) → station payable → (two people approve) → station bank account, with the audit log under it. A dot travels through each gate. Then four more checks (recorded as pumped, piastres, limits, tenant isolation).
8. **Made in Egypt.** Now a warm neutral section; only the Cairo clock tile is on the stage.
9. **Pilot programme** (stage). "We're starting with a small group of fleets and stations": set-up with our team, a direct line to the people building it, terms agreed in writing before going live. Apply for the pilot (→ form) or WhatsApp. This replaces any idea of logos, numbers or testimonials: there are no customers yet and the page says nothing it cannot prove.
10. **Pricing.** Unchanged.
11. **FAQ.** Unchanged.
12. **Request a demo.** The consent line links to the privacy policy.
13. **Footer.** Product, Contact and Legal columns; every link is at least 40 px tall; language and theme switchers.

## Colour balance (owner, 2026-11-21)

The green stays the brand, but it no longer fills the page. Public pages draw their brand surface from the **stage** tokens (`tokens.css`, last two blocks, both themes, in the contrast test): `stage`, `stage-foreground`, `stage-muted`, `stage-line`, `stage-panel`, `stage-tint` and the glows `stage-glow-1/2/3`. In Light the stage is the petroleum green; in Dark it is a calm deep neutral (`night-950`) and the green only glows. Light uses two stage bands (hero, pilot) between warm neutral sections. The portals' `hero` and `hero-*` tokens are untouched.

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
| Stage background | Three radial glows drifting with `transform` only (`src/public/stage.css`, shared with sign-in); the glow colours are registered `@property` colours so they cross-fade with the theme | 22–32 s, alternate, paused off screen |
| Entrance | Headline, lead, actions and assurances rise and unblur in sequence; the collage rises last | 900–1100 ms, exponential ease-out |
| Product collage | Phase timeline (≈ 12.3 s loop), litre counter via `requestAnimationFrame` written to a text node, the new statement row with `zood-arrive`, receipt by `clip-path` | Pauses off screen and in background tabs |
| Header | Background, border, shadow and blur transition when it firms up; the speed line slides under the active link | 300 ms / 260 ms |
| Mobile menu | Fades in; links rise one after another | 160 ms, 35 ms stagger |
| Scroll reveals | Opacity + 14 px rise, only after `data-reveal-ready` | 600 ms |
| Money path | A dot travels along each gate's line | 3.6 s loop |
| Main call to action | Lifts 1 px, the arrow nudges towards the reading direction, a soft light passes once (`.public-cta`) | 220 ms / 700 ms |
| Light ↔ Dark | The public theme button runs the change as a View Transition (`switchThemeSmoothly`): one 200 ms cross-fade of two snapshots, cheap even on this long page. Other switches (the footer's segmented control, browsers without View Transitions) get `useThemeCrossfade`: for 240 ms `<html data-public-theme-fade>` turns on colour transitions; it steps aside while a View Transition runs or when the portals' global `data-theme-switching` is present. Measured in Chromium: the snapshot cross-fade is ready about 160 ms after the press, and the long frame on a switch comes from restyling the page itself, with or without the fade | 200 ms |
| FAQ | Radix accordion height | 200–260 ms |

**Scroll reveals never leave blank areas.** Content is visible by default. `useScrollReveal` first shows everything already on screen or above it, then sets `data-reveal-ready` on the page; only then does the CSS hide what is still below the fold. Safety nets: a sweep after every scroll settles (anchor jumps, fast flicks), one after 1.2 s, and everything on `beforeprint`. An e2e test jumps to `#faq` and checks that nothing in view is hidden.

**Anchors.** `scroll-padding-top` is 5 rem (5.5 rem from lg) so a section heading lands below the fixed header; `scroll-behavior: smooth` except under reduced motion.

Under `prefers-reduced-motion` all of it is off: no reveal hiding, no drift, no cross-fade, no travelling dot, and the collage shows the finished fill-up with the receipt printed.

## Tokens added (both themes)

`--hero-foreground`, `--hero-muted` (text on the hero, in the contrast test), `--hero-line` and `--hero-panel` (hairlines and glass panels on the hero), `--hero-glow-1`, `--hero-glow-2`, `--hero-glow-3` (the moving glows: brand greens and the logo orange). Utilities: `text-hero-foreground`, `text-hero-muted`, `border-hero-line`, `bg-hero-panel`.

## Crawlers and sharing

`index.html` carries a static Arabic title and description and the Open Graph and Twitter tags (crawlers and link previews do not run JavaScript); the landing still sets the title and description in the visitor's language at run time and restores them when it unmounts. The share card is `public/og-card.png` (1200 × 630), rendered from `scripts/og/og-card.html` with the app's fonts and logo (`node scripts/og/render.mjs`). `public/robots.txt` keeps the portals, the POS and `/demo` out and points at `public/sitemap.xml` (`/`, `/privacy`, `/terms`). Caddy's `try_files {path} /index.html` serves these files before the SPA fallback; `src/public/site.test.ts` checks that, and that the origin is the same in all four files.

## Placeholders the owner must replace

| What | Where |
|---|---|
| Tier prices per vehicle per month (150 / 130 / 115 / 100 EGP, custom for 250+) | `src/landing/pricing.ts` (`PLACEHOLDER` comments) |
| Yearly = 10 months | `YEARLY_BILLED_MONTHS` in the same file |
| RFID tag price (250 EGP one-time) | `TAG_PRICE_PER_VEHICLE` in the same file |
| "Indicative" labels | Remove from `Pricing.tsx` and `landing.json` once prices are final (`PRICES_ARE_PLACEHOLDERS`) |
| Contact WhatsApp and e-mail (temporary) | `src/landing/contact.ts` |
| Station joining terms ("agreed with each station") | `pricing.stationsNote` in `landing.json` |
| The site's origin (now the zawed-dev web service) | `SITE_ORIGIN` in `src/public/site.ts`, and the same value in `index.html`, `public/robots.txt`, `public/sitemap.xml` |
| Legal entity name, registered address, commercial registration, hosting provider and region, demo-request retention period, refund terms, competent courts | The `[bracketed]` items in `src/i18n/locales/{ar,en}/legal.json` (shown highlighted on `/privacy` and `/terms`), and the "Draft" banner there once they are filled |
| Pilot terms (length, scope, price) | Agreed per customer; the page only promises they are agreed in writing first |

## Performance

The landing route is its own chunk (JS about 98 kB, 28 kB gzip, including both languages' copy and the accordion; CSS 4 kB plus the shared 2 kB stage CSS). The legal pages and the sign-in brand panel are their own chunks too. The entry chunk only gains the names of the new lazy chunks in Vite's preload map (+0.3 kB raw, +0.1 kB gzip). No images on the page apart from the share card (only fetched by crawlers), no video, no animation library; all motion is CSS transforms and opacity plus two `requestAnimationFrame` counters that stop off screen.
