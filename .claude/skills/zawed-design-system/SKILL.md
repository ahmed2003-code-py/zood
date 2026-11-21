---
name: zawed-design-system
description: The ZAWED (زوّد) web design system in apps/web — semantic colour tokens, the Light/Dark/POS themes, how to add a token, the shared components, the bilingual Arabic RTL + English LTR rules, value formatting (money, litres, plates, dates), charts and motion. Use it before touching anything visual in apps/web — a colour, a class, a component, a screen, a string, tokens.css or index.css — even for a small styling fix, because lint and tests reject raw colours, physical direction classes, the dark: variant and untranslated text.
---

# ZAWED design system

Arabic RTL and English LTR are equal first-class layouts. Light, Dark and System themes are core.
A component is finished only when it is correct in all four combinations.

## 1. Where things live

| What | File |
|---|---|
| Raw colours and semantic tokens (the only place raw colours are allowed) | `apps/web/src/styles/tokens.css` |
| Token → Tailwind utility mapping, fonts, keyframes, reduced-motion rule | `apps/web/src/styles/index.css` |
| Shared components | `apps/web/src/design-system/` (exported from `index.ts`) |
| Theme preference and `data-theme` | `apps/web/src/theme/` |
| Strings | `apps/web/src/i18n/locales/{ar,en}/<namespace>.json` |
| Number, money, volume and date formatting | `apps/web/src/lib/format` |
| Lint rules that enforce this file | `apps/web/eslint.config.js` |
| Live preview of every component | `/dev/ui` and `/dev/ui/canvas` |
| Logo and brand colours | `docs/brand/README.md` |

Primitive variables and keyframes keep the `--zood-` / `zood-` prefix from before the rebrand. Do not rename them.

## 2. Colour: pick by meaning

Tailwind's default palette is removed (`--color-*: initial`). Only these utilities exist:

| Meaning | Utilities |
|---|---|
| Page, card, raised card, recessed area | `bg-background`, `bg-surface`, `bg-surface-elevated`, `bg-surface-sunken` |
| Row hover, table header band and toolbars | `bg-surface-hover`, `bg-surface-muted` |
| Body text, secondary text | `text-foreground`, `text-muted` |
| Hairline, stronger outline | `border-border`, `border-border-strong` |
| Main action | `bg-primary` + `text-primary-foreground`, hover `bg-primary-hover` |
| Tinted brand area | `bg-primary-subtle` + `text-primary-subtle-foreground` |
| Accent (logo orange) | `bg-accent` + `text-accent-foreground` |
| Status | `success`, `warning`, `danger`, `info`, each with a `-subtle` background; `danger-foreground` on a `bg-danger` fill |
| Sidebar and the green hero | `sidebar`, `sidebar-foreground`, `sidebar-muted`, `sidebar-active`, `sidebar-active-foreground`, `hero` |
| Text, hairlines and glass panels on the hero (landing page) | `text-hero-foreground`, `text-hero-muted`, `border-hero-line`, `bg-hero-panel`; the moving glows read `--hero-glow-1/2/3` in CSS |
| Inputs and focus | `bg-input`, `border-input-border`, `focus-ring` |
| Overlay behind dialogs | `bg-overlay` |
| Not themed, on purpose | `device-bezel` (POS hardware frame), `paper` and `paper-ink` (thermal receipt) |

Shape and depth (U1, `docs/design/u1-direction.md`):

| Layer | Radius | Shadow |
|---|---|---|
| Button, input, segmented control | `rounded-control` (10 px) | `shadow-control` |
| Card, panel, list, chart | `rounded-container` (14 px) | `shadow-card` with a `border-border` hairline |
| Popover, menu, toast | `rounded-container` | `shadow-elevated` |
| Dialog and bottom sheet | `rounded-dialog` (18 px) | `shadow-dialog` |

In Dark the card and control shadows are empty on purpose: depth comes from lighter surfaces and
borders. Declare elevation once: never a wide shadow under a border.

Type scale: `text-xs` (12, captions and chips), `text-label` (13, labels and table headers),
`text-sm` (14, body and cells), `text-[0.9375rem]` (15, inputs and buttons), `text-base` (16, section
titles), `text-title` (17, dialog titles), `text-xl md:text-2xl` (page titles), `text-kpi` (28, KPI
values), `text-display` (36, the wallet balance). Figures in tables are tabular by default.

**The speed line.** A short orange bar (`h-1 w-4 rounded-full bg-accent`, or `w-[3px]` upright in the
sidebar) means "this one": the active sidebar item, the active tab and the main KPI. Use orange for
nothing else in the interface.

Status is always icon + text + colour, never colour alone. Use `StatusChip` with a `Tone`
(`success`, `warning`, `danger`, `info`, `neutral`, `brand`).

## 3. Themes

- A theme is `data-theme="light"` or `"dark"` on any element, normally `<html>`. A subtree can carry its own theme: the POS stays Light inside a Dark page.
- There is no `dark:` variant. A light/dark difference is a token value, never a class.
- The POS is its own surface: `data-surface="pos"` gives larger controls (`--pos-button-h`, `--pos-key-h`, `--pos-value-size`) and `Button size="pos"`. It has its own theme preference, defaults to Light because it is used outdoors, and its Dark theme must reach 7:1 for text.

## 4. Adding or changing a token

1. If a new raw colour is needed, add a primitive under `:root` in `tokens.css`.
2. Add the semantic token in **both** blocks: `:root, [data-theme='light']` and `[data-theme='dark']`.
3. Map it in `@theme inline` in `index.css` (`--color-<name>: var(--<name>)`) so the utility exists.
4. If it is used as text on a background, or as a border or mark, add the pair to `PAIRS` in `src/styles/tokens.contrast.test.ts` (4.5:1 for text, 3:1 for borders, marks and focus).
5. Run `npm run test` in `apps/web`. The contrast test runs for both themes.

## 5. Two languages, two directions

- No literal text in JSX. Every string is a key, added to **both** `ar` and `en` in the same change. A test checks that both languages have the same namespaces and keys, that Arabic plurals define all six forms, and that no value is empty.
- Logical utilities only: `ms-`, `me-`, `ps-`, `pe-`, `start-`, `end-`, `text-start`, `text-end`, `border-s`, `border-e`, `rounded-s`, `rounded-e`.
- Fonts: IBM Plex Sans Arabic first in Arabic, IBM Plex Sans first in English. Arabic text inside an English layout keeps the Arabic face through `:lang(ar)`.
- Digits are Western (0–9) in both languages.

## 6. Values

| Value | Rule | Use |
|---|---|---|
| Money | Integer piastres in code, formatted for display only | `<Money>` or `formatMoney` |
| Fuel volume | Integer millilitres in code | `<Volume>` or `formatVolume` |
| Vehicle plate | Shown exactly as registered in every language, never transliterated | `<Plate>` |
| Date and time | Stored as ISO UTC, shown in `Africa/Cairo` | `<DateTimeText>` or the `format*` date helpers |

Never format a number, amount or date by hand, and never use floating-point arithmetic on money or volume (`src/domain/money.ts`).

On the POS the wallet is shown to the agent as a status word (available, low, insufficient, suspended), never as an amount.

## 7. Components

Reuse before building: `Button`, `IconButton`, `TextField`, `TextAreaField`, `SelectField`, `NativeSelect`, `FileField`, `SegmentedControl`, `Numpad`, `Panel`, `KpiTile`, `List`, `ListRow`, `RecordRow`, `FilterBar`, `DataView`, `RowsSkeleton`, `Tabs`, `Stepper`, `Sheet`, `StatusChip`, `TransactionStatusChip`, `TierChip`, `ConnectivityChip`, `Banner`, `EmptyState`, `ErrorState`, `Skeleton`, `Toaster` and `toast`, `ChartFrame`, `ChartTooltip`, `Icon`, `BrandLogo`, `BrandIcon`, `LanguageSwitcher`, `ThemeSwitcher`. A text control that cannot use these takes `fieldControlClass()` so it looks the same.

- Lists of records use `DataView`: a table on wide screens and a list of `ListRow` on phones. Numbers and amounts align to the inline end. The table header stays in view under the page header, so the list's container uses `overflow-clip`, never `overflow-hidden` (`ListPanel` and `Panel` already do).
- Filters for a list go in a `FilterBar` as the first child of its `ListPanel`, so they read as the list's toolbar.
- A record with a status and buttons (a device, an agent, a top-up) is a `RecordRow`: the record at the start, status and buttons together at the inline end. It lays itself out by its column's width (container query), so it works in a side column too.
- Forms and details open in `Sheet`, a centred dialog from 640 px and a bottom sheet on phones. Pick a `size`: `sm` for a reason or a confirmation (`ReasonSheet` does), `md` (default) for a short form or a detail, `lg` for a long form in two columns, `xl` for a table. Never a side panel.
- Page tops use `PageHeader` (title, description, actions, optional badge). A detail page adds a `BackLink` above it. Sections use `PageSection` with an optional `description` instead of a loose paragraph.
- Every data screen has four states: loading (`RowsSkeleton` for lists, `Skeleton` for the rest), empty (`EmptyState`), error (`ErrorState`, which uses the danger tone) and loaded.
- Buttons: one `primary` per area. A disabled filled button turns neutral, never a faded brand colour. `ghost` is neutral text; do not colour it to mean "positive".
- A value in the other direction (a reference, a code, a Latin legal name in Arabic) is wrapped in `<bdi>` or a `span dir="ltr"` inside the line, never by giving the whole paragraph `dir="ltr"`: that moves it to the wrong edge.
- Icons come through `Icon` (lucide). An icon-only button is an `IconButton` with an accessible label.

## 8. Charts

- Wrap every chart in `ChartFrame`. It reads the `--chart-*` tokens from the current theme, so a chart never hard-codes a colour.
- Plotting is always left to right, in Arabic too. Only the labels are translated.
- Charts are lazy-loaded so the screen renders first.

## 9. Motion

Motion explains a change. It is short and calm, and it never delays the user.

| Use | Keyframes | Timing in use |
|---|---|---|
| Dialog opening from 640 px | `zood-dialog-in` (fade, 97 % scale, 8 px rise) | 180 ms, `cubic-bezier(0.22,1,0.36,1)` |
| Dialog closing from 640 px | `zood-fade-out` | 120 ms, ease-in |
| Bottom sheet on phones | `zood-sheet-from-bottom` in, `zood-sheet-to-bottom` out | 240 ms in (same curve), 160 ms out, ease-in |
| Navigation drawer on tablets (direction follows `dir`) | `zood-sheet-from-start` (`zood-sheet-from-end` is kept for side drawers) | 220 ms, ease-out |
| Overlays and small reveals | `zood-fade-in` in, `zood-fade-out` out | 120–150 ms |
| Toasts | `zood-dialog-in` | 180 ms |
| Hover and press | colour transitions | 150 ms |
| Data that just arrived, such as a transaction from the POS | `zood-arrive` | once, a brief background fade |
| RFID scan in progress | `zood-pulse-ring` | 1.4 s, repeating while scanning |
| Receipt printing | `zood-print` | 1.4 s |

- Reuse these keyframes before adding one. Apply them as `animate-[<name>_<duration>_ease-out]`.
- `prefers-reduced-motion` switches all animation and transition off in one rule in `index.css` (duration 0.01 ms, one iteration, so infinite animations stop at their end state). New motion must not depend on running to be understood.
- The public landing page (`src/landing/landing.css`, loaded with its chunk only) has its own, livelier motion: `landing-drift-a/b/c` (hero glows, 22–32 s), `landing-rise` and `landing-device` (entrance), scroll reveals through `[data-reveal]` and `data-revealed`, and `landing-accordion-open/close`. Pass 3 added the scroll-driven fuel line (`--how` view timeline), the hero speed lines, card lift with a pointer glow, and one small motion per landing glyph. They stay out of the portals; under reduced motion the landing turns all of them off. See `docs/design/landing-spec.md`.
- The landing has its own duotone icon set, `src/landing/glyphs.tsx` (`<Glyph name size>`), coloured by the `--glyph-*` tokens. Use it only on public pages; the portals keep `Icon` (lucide).

## 10. What fails automatically

| Mistake | Caught by |
|---|---|
| `#hex`, `rgb(`, `hsl(`, `oklch(` in a `.ts` or `.tsx` file | lint |
| `ml-`, `pr-`, `left-`, `text-right`, `rounded-l` and other physical classes | lint |
| `dark:` | lint |
| Literal text in JSX | lint |
| A key missing in one language, a missing Arabic plural form, an empty string | `i18n.parity.test.ts` |
| A text/background pair below its contrast minimum | `tokens.contrast.test.ts` |
| A key screen that changed its look | `e2e/visual.spec.ts` (demo) and `e2e/live/visual.spec.ts` (live screens on the API stub, `npx playwright test -c playwright.live.config.ts`). Update the baselines with `--update-snapshots` only for an intended change. |

Run `npm run check` in `apps/web` before calling UI work done. For a review checklist use `zawed-ui-audit`.
