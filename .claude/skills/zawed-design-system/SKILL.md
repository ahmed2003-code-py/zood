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
| Body text, secondary text | `text-foreground`, `text-muted` |
| Hairline, stronger outline | `border-border`, `border-border-strong` |
| Main action | `bg-primary` + `text-primary-foreground`, hover `bg-primary-hover` |
| Tinted brand area | `bg-primary-subtle` + `text-primary-subtle-foreground` |
| Accent (logo orange) | `bg-accent` + `text-accent-foreground` |
| Status | `success`, `warning`, `danger`, `info`, each with a `-subtle` background; `danger-foreground` on a `bg-danger` fill |
| Sidebar and the green hero | `sidebar`, `sidebar-foreground`, `sidebar-muted`, `sidebar-active`, `sidebar-active-foreground`, `hero` |
| Inputs and focus | `bg-input`, `border-input-border`, `focus-ring` |
| Overlay behind sheets | `bg-overlay` |
| Not themed, on purpose | `device-bezel` (POS hardware frame), `paper` and `paper-ink` (thermal receipt) |

Shape and depth: `rounded-control` (buttons, inputs), `rounded-container` (cards, sheets), `shadow-elevated`.

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

Reuse before building: `Button`, `IconButton`, `TextField`, `SegmentedControl`, `Numpad`, `Panel`, `KpiTile`, `List`, `ListRow`, `DataView`, `Tabs`, `Stepper`, `Sheet`, `StatusChip`, `TransactionStatusChip`, `TierChip`, `ConnectivityChip`, `Banner`, `EmptyState`, `ErrorState`, `Skeleton`, `Toaster` and `toast`, `ChartFrame`, `ChartTooltip`, `Icon`, `BrandLogo`, `BrandIcon`, `LanguageSwitcher`, `ThemeSwitcher`.

- Lists of records use `DataView`: a table on wide screens and a list of `ListRow` on phones. Numbers and amounts align to the inline end.
- Every data screen has four states: loading (`Skeleton`), empty (`EmptyState`), error (`ErrorState`) and loaded.
- Icons come through `Icon` (lucide). An icon-only button is an `IconButton` with an accessible label.

## 8. Charts

- Wrap every chart in `ChartFrame`. It reads the `--chart-*` tokens from the current theme, so a chart never hard-codes a colour.
- Plotting is always left to right, in Arabic too. Only the labels are translated.
- Charts are lazy-loaded so the screen renders first.

## 9. Motion

Motion explains a change. It is short and calm, and it never delays the user.

| Use | Keyframes | Timing in use |
|---|---|---|
| Sheets and drawers (direction follows `dir`) | `zood-sheet-from-end`, `zood-sheet-from-start`, `zood-sheet-from-bottom` | 200–220 ms, ease-out |
| Overlays and small reveals | `zood-fade-in` | 120–150 ms, ease-out |
| Data that just arrived, such as a transaction from the POS | `zood-arrive` | once, a brief background fade |
| RFID scan in progress | `zood-pulse-ring` | 1.4 s, repeating while scanning |
| Receipt printing | `zood-print` | 1.4 s |

- Reuse these keyframes before adding one. Apply them as `animate-[<name>_<duration>_ease-out]`.
- `prefers-reduced-motion` switches all animation and transition off in one rule in `index.css`. New motion must not depend on running to be understood.

## 10. What fails automatically

| Mistake | Caught by |
|---|---|
| `#hex`, `rgb(`, `hsl(`, `oklch(` in a `.ts` or `.tsx` file | lint |
| `ml-`, `pr-`, `left-`, `text-right`, `rounded-l` and other physical classes | lint |
| `dark:` | lint |
| Literal text in JSX | lint |
| A key missing in one language, a missing Arabic plural form, an empty string | `i18n.parity.test.ts` |
| A text/background pair below its contrast minimum | `tokens.contrast.test.ts` |

Run `npm run check` in `apps/web` before calling UI work done. For a review checklist use `zawed-ui-audit`.
