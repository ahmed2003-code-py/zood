---
name: zawed-ui-audit
description: Checklist for auditing or reviewing any ZAWED (زوّد) web screen or UI diff in apps/web — tokens, Arabic RTL and English LTR, Light and Dark, the POS surface, value formatting, component states, accessibility, phone and desktop widths, motion. Use it whenever you review, critique, QA or "check" a page, component or UI change, before marking UI work done, or when someone asks "does this look right" or "is this ready", even if they do not say "audit".
---

# ZAWED UI audit

Check the changed files **and** the rendered screen. Open each changed screen in four states:
Arabic + Light, Arabic + Dark, English + Light, English + Dark. Report findings as
`file:line — problem — fix`, most severe first. The rules behind each item are in `zawed-design-system`.

## 1. Colour and themes
- [ ] Only semantic token utilities (`bg-surface`, `text-foreground`, …). No raw colour, no `dark:`.
- [ ] Status shows icon + text + colour through `StatusChip` or a `Tone`, never colour alone.
- [ ] Text on a fill uses its partner (`primary-foreground` on `primary`, `danger-foreground` on `danger`).
- [ ] Correct in Light and Dark. A POS screen is also correct in POS Light (the default) and POS Dark.
- [ ] A new token exists in both theme blocks and has a contrast pair in `tokens.contrast.test.ts`.

## 2. Language and direction
- [ ] No literal text in JSX; every new key exists in `ar` and `en`; Arabic plurals have all six forms.
- [ ] No raw i18n key visible on screen in either language.
- [ ] Logical utilities only (`ms/me/ps/pe/start/end`); the layout mirrors correctly in RTL.
- [ ] Icons that carry a direction (arrows, chevrons) point the right way in both directions (`IconButton directional`).
- [ ] Arabic copy is natural, short and consistent with the same term elsewhere.

## 3. Values
- [ ] Money through `<Money>` / `formatMoney`; volume through `<Volume>` / `formatVolume`; dates through `<DateTimeText>` or the format helpers.
- [ ] Western digits in both languages.
- [ ] Plates through `<Plate>`, exactly as registered, never transliterated.
- [ ] No arithmetic on formatted values, and no floating point on piastres or millilitres.
- [ ] On the POS the wallet is a status word, never an amount.

## 4. States
- [ ] Loading (`Skeleton` shaped like the content), empty (`EmptyState` with a next step), error (`ErrorState`), loaded.
- [ ] Hover, `focus-visible` ring, pressed and disabled on every control.
- [ ] A destructive or irreversible action asks for confirmation.

## 5. Accessibility
- [ ] Icon-only buttons have an accessible name (`IconButton` label).
- [ ] Text contrast at least 4.5:1, borders and marks at least 3:1; POS Dark text at least 7:1.
- [ ] Sheets trap focus, return it on close, and close with Escape.
- [ ] Tables have a caption; form fields have labels and show their error next to the field.

## 6. Layout
- [ ] No horizontal overflow at 390, 768 and 1440 px, in Arabic and in English.
- [ ] Record lists use `DataView`: a table on wide screens, list rows on phones; numbers align to the inline end.
- [ ] POS controls keep their large size (`Button size="pos"`, numpad keys); nothing small to tap outdoors.
- [ ] Radius from `rounded-control` / `rounded-container`; depth from `shadow-elevated`.

## 7. Charts
- [ ] Inside `ChartFrame`, colours from `--chart-*`, plotted left to right in both languages.
- [ ] Lazy-loaded; the screen renders before the chart.

## 8. Motion
- [ ] Uses the existing `zood-*` keyframes with short ease-out timings.
- [ ] Still understandable with `prefers-reduced-motion`.

## 9. The receipt (when it changes)
- [ ] Compact 58 mm layout; a new line needs a reason (`docs/demo/overnight-decisions.md`, decision 13).
- [ ] Printed in the transaction's `receiptLocale`; a reprint uses the same language.
- [ ] A printer failure never changes the transaction; the digital receipt is offered.

Finish by running `npm run check` in `apps/web`, and the E2E screens if the layout changed.
