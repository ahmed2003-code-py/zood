# ZOOD — زُود

B2B fleet fuel management platform. This repository currently holds the **investor demo** (interactive prototype with mock data).

## Structure

```
apps/web/        React + TypeScript + Vite — company, station, operations portals and the POS simulator
docs/            architecture, hardware spike, design prompts
```

## Run the web app

Requires Node.js 22+.

```bash
cd apps/web
npm install
npm run dev          # http://localhost:5173 → design-system preview at /dev/ui
```

| Command | What it does |
|---|---|
| `npm run check` | Typecheck, lint, unit tests |
| `npm run build` | Production build |
| `npm run e2e` | Playwright tests (first time: `npx playwright install chromium`) |

## Investor demo

Open `/demo`. Runbook, presenter controls (Ctrl+Shift+P) and the 10-minute sequence: [docs/demo/investor-demo.md](docs/demo/investor-demo.md). Overnight decisions: [docs/demo/overnight-decisions.md](docs/demo/overnight-decisions.md).

## Conventions (apps/web)

- **Bilingual:** no hardcoded UI text. Strings live in `src/i18n/locales/{ar,en}/<namespace>.json`; add both languages in the same change (a test enforces key parity).
- **Direction:** use logical utilities (`ms-`, `pe-`, `start-`, `text-end`, …). Physical ones (`ml-`, `left-`, `text-right`) fail lint.
- **Colors:** semantic tokens only (`bg-surface`, `text-foreground`, …), defined in `src/styles/tokens.css`. Raw colors outside that file fail lint; contrast is tested in both themes.
- **Formatting:** money, liters and dates go through `src/lib/format` (Western digits in both languages). Money is integer piastres, volume is integer milliliters.
- **Charts:** plotting is always left-to-right in both languages (see `ChartFrame`).
- **Plates:** shown exactly as registered in every language (`<Plate>`).
