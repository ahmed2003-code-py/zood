# Sign-in page (UI pass 2)

The owner called the old sign-in page "very, very primitive and ugly" (2026-11-21). The new one keeps
every behaviour and gives the visitor a clear picture of what they are signing in to.

Code: `apps/web/src/auth/AuthLayout.tsx` (the frame), `src/auth/brand/` (the panel, lazy),
`src/public/` (shared with the landing page). Copy: `src/i18n/locales/{ar,en}/site.json`.

## Where it is used

`AuthLayout` frames `/login` (credentials, second factor, enrolment), `/account/password` and the
"wrong portal" screen (`NoAccess`, now loaded lazily from `RequirePortal` so the portal chunks stay light).

## Layout

| Width | Layout |
|---|---|
| From lg (1024 px) | Two columns. The inline start (right in Arabic) holds the header (logo back to `/`, language, theme), the form and a footer (home, privacy, terms, contact). The inline end holds the brand panel: full height, sticky, inset 12 px with 28 px corners, on the stage. |
| Below lg | The header sits on a short stage band (headline and the four-step rail), and the form rises over it as a sheet with rounded top corners. |

The DOM order is header, form, footer, panel, so keyboard and screen-reader users reach the form
first. The grid places the panel visually. The form arrives with a short `zood-dialog-in`.

Under the credentials form, after a hairline: three chips (Fleets, Stations, Operations) and one
sentence saying this is the one sign-in for fleet companies, partner stations and the ZAWED team, and
that the account decides which area opens.

## Brand panel

- Headline: "Every litre, from the tag to the ledger." The lead line shows only on screens at
  least 960 px tall.
- **Vignette** (`FuelingVignette.tsx`, `vignette-phase.ts`): a fill-up as the product records it.
  - A four-step rail (tag read → litres counted → wallet debited → receipt issued). The orange line
    fills up to the current step, and the current step breathes.
  - A card built from design-system parts: the POS, the vehicle's plate, a litre counter with its
    bar, and the balanced ledger entry (company wallet −, station payable +).
  - A receipt strip slides out from under the card.
  - Labelled "Illustration", with every value a sample. One screen-reader sentence describes it.
- **Three product truths**, with no numbers: checked at the pump, posted as it happens, a second
  person on every money move. The body text hides on screens under 880 px tall.
- On phones only the headline and the rail show.

The panel is its own chunk, loaded with `React.lazy`. While it loads, an empty stage block of the
same size holds its place, so nothing moves when it arrives.

## Motion

The stage glows come from `src/public/stage.css`. The fill-up loop is about 11.6 s, and the litre
counter uses `requestAnimationFrame` written straight to the text nodes. The card floats 6 px over
7 s. Everything pauses off screen and in a background tab. Under `prefers-reduced-motion` nothing
moves, and the panel shows the finished fill-up with the receipt issued. An e2e test checks this,
and checks that no animation is running.

Light ↔ Dark cross-fades for about 200 ms (`useThemeCrossfade`). Under reduced motion the switch is
instant.

## Colours

The panel uses the stage tokens. In Light it is the petroleum green with green and orange glows. In
Dark it is a deep neutral (`night-950`) where the green only glows. The form side uses the ordinary
page tokens.

## Accessibility

- The `h1` is the form title, and the panel is an `aside` with its own `h2`.
- Controls are 40 px tall, 44 px on touch screens. The submit button is `size="lg"`.
- The language button's accessible name contains its visible text ("Language: English").
- There is no horizontal scroll at 360 px. An e2e test checks the Arabic layout at that width.

## Tests

`e2e/sign-in.spec.ts` covers four things:

- the old `/admin/login` bookmark still works, `next` included;
- the desktop layout: form first, panel beside it, keyboard order;
- the phone layout at 360 px: band above the form, no overflow, large targets;
- reduced motion.
