# زوّد / ZAWED brand assets

The logo follows the brand reference (speed lines, a fuel drop in the و, the shadda above the و,
"ZAWED" underneath with an accent W stroke), **recolored to the existing product palette**. The
product's tokens are the source of truth; there is no separate brand palette.

| Logo part | Light | Dark | On brand green (sidebar, /demo hero) |
|---|---|---|---|
| Wordmarks (زوّد, ZAWED) | `--brand-mark` #0F3B2C | `--brand-mark` #EEEAE2 | `--sidebar-active-foreground` #FFFFFF |
| Speed lines, drop reflection, W stroke | `--accent` #F28C28 | `--accent` #F5A04A | `--accent` |
| Drop | cut out of the و (shows the surface) | same | same |
| App icon | `--primary` square, `--primary-foreground` W and shadda, `--accent` W stroke | | |

The Arabic name is exactly **زوّد** (U+0632 U+0648 U+0651 U+062F): only the shadda.

## Files

- `zawed-logo-light.svg`, `zawed-logo-dark.svg`, `zawed-logo-inverse.svg`: full logo
- `zawed-logo-compact-light.svg`, `zawed-logo-compact-dark.svg`: Arabic wordmark only
- `zawed-app-icon.svg`: square icon (also `apps/web/public/favicon.svg`)
- In the app: `BrandLogo` (variants `full` / `compact`, tones `default` / `inverse`) and `BrandIcon`
  in `apps/web/src/design-system/brand/`. They use the theme tokens directly, so they follow Light/Dark.

## Regenerating

The glyph outlines come from the app's own fonts (IBM Plex Sans Arabic Bold, IBM Plex Sans Bold)
shaped with HarfBuzz, so Arabic joining and the shadda position are typographically correct.

```bash
# in an empty temporary folder
npm init -y && npm install harfbuzzjs
node <repo>/apps/web/scripts/brand/gen.mjs <repo>/apps/web      # writes raw.json (outlines)
node <repo>/apps/web/scripts/brand/compose.mjs <repo>/apps/web  # writes geometry, SVGs, favicon
```

`docs/brand/zood-logo-reference.png` is the earlier ZOOD reference, kept for history.
