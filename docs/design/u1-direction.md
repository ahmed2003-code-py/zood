# U1 visual direction

ZAWED is a working tool for fleet managers, accountants, station managers and our own operations
team. They come to check a balance, approve a transfer or find one fueling, often between other
jobs. The portals should feel like a calm, well-kept Egyptian bank app: quiet surfaces, clear
numbers and one brand colour that you recognise straight away. Nothing should feel improvised.

The brand stays as it is: petroleum green `#0F3B2C`, the logo orange, warm sand neutrals, IBM Plex
Sans Arabic and IBM Plex Sans. U1 refines how they are used. It adds no new palette.

## Signature

The orange **speed line** from the logo marks "you are here": the active sidebar item, the active
tab and the one figure that matters most on a screen. Orange is used for nothing else in the
interface apart from the logo, so it always means "this one".

The green **balance card** (the wallet hero) is the one filled brand surface inside a page. The
sidebar is the other one.

## Type scale

One family per language. Fixed rem sizes, ratio about 1.2. Numbers use tabular figures everywhere
they line up (tables, statements, KPI values).

| Role | Size / line | Weight | Utility |
|---|---|---|---|
| Hero figure (wallet balance) | 36 / 40 | 600 | `text-display` |
| Page title | 24 / 32 (20 on phones) | 600, tracking -0.01em | `text-xl md:text-2xl` |
| KPI value | 28 / 34 | 600 | `text-kpi` |
| Section and dialog title | 17 / 24 | 600 | `text-title` |
| Body, table cells, form input | 14–15 / 20–24 | 400–500 | `text-sm`, `text-[0.9375rem]` |
| Labels and table headers | 13 / 18 | 500 | `text-label` |
| Captions, hints, chips | 12 / 16 | 400–500 | `text-xs` |

## Spacing

A 4 px grid. Groups are tight and sections are far apart:

- inside a control or chip: 4–8;
- between a label and its field, or a title and its description: 6–8;
- between fields in a form: 16–20; between rows of a detail list: 0 with a hairline;
- card padding: 16 on phones, 20 from `md`;
- between page sections: 24 on phones, 32 from `md`; the page header sits 24–32 above the content.

## Radius

`rounded-control` 10 px (buttons, inputs, segmented controls), `rounded-container` 14 px (cards,
panels, tables), `rounded-dialog` 18 px (dialogs and bottom sheets), full for chips and avatars.

## Elevation and surfaces

Four layers, each declared once (a border or a shadow, never a wide shadow under a border):

| Layer | Light | Dark |
|---|---|---|
| Page | `background`: warm sand | `background`: near-black green |
| Card, table, panel | `surface` white, hairline border, `shadow-card` (1 px, barely there) | `surface` one step lighter, hairline border, no shadow |
| Popover, toast, menu | `surface-elevated`, `shadow-elevated` | `surface-elevated`, border ring |
| Dialog | `surface-elevated`, `shadow-dialog` over `overlay` | same, deeper overlay |

In Dark, depth comes from lighter surfaces and borders rather than shadows. The Dark sidebar keeps
a green tint so the brand is still there at night.

## Focus

Every interactive element shows a 2 px `focus-ring` outline, offset 2 px, on keyboard focus only.
Inputs also tint their border with `focus-ring` and add a soft 3 px halo of the same colour.

## Motion

Short and calm (see the design-system skill for the table). Hover and press: 150 ms colour changes.
Dialogs: the overlay fades in over 150 ms and the dialog scales from 97 % and fades in over 180 ms
with a strong ease-out. Closing is a 120 ms fade. On phones the dialog rises from the bottom.
Reduced motion turns all of it off.

## Density for data screens

- Table rows are 52 px high, with 14 px text, a 40 px header row in a sunken tint and a hover tint.
  The header stays in view while the page scrolls.
- Numbers, amounts and volumes align to the inline end, in tabular figures.
- Identifiers (references, plates, codes) keep their own direction and never wrap.
- On phones a table becomes a list of rows: title and subtitle at the start, the amount and the
  status chip at the end.
- Filters sit in one toolbar above the list; on phones the less used ones fold away.

## Dialogs instead of side panels

Forms and details open in a centred dialog (sizes `sm` 26 rem, `md` 32 rem, `lg` 42 rem, `xl`
56 rem). The header and footer stay put while the body scrolls. On phones it is a bottom sheet.
A short reason or confirmation uses `sm`, an ordinary form `md`, a long form with two columns `lg`,
and a table or a wide detail `xl`.

## Iconography

Lucide, 1.75 stroke, 16 px in dense rows and chips, 18 px in buttons and navigation, 20 px on
their own. Icons sit in a tinted rounded square when they label a row or an empty state. Status is
always icon, text and colour together.
