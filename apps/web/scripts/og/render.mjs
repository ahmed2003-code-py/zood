// Renders og-card.html to public/og-card.png (1200 × 630) with the app's own fonts and logo.
// Run from apps/web: node scripts/og/render.mjs
import { readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const web = resolve(here, '../..');
const require = createRequire(resolve(web, 'package.json'));
const { chromium } = require('@playwright/test');

const font = (pkg, file) => pathToFileURL(resolve(web, 'node_modules/@fontsource', pkg, 'files', file)).href;
const logo = pathToFileURL(resolve(web, '../../docs/brand/zawed-logo-inverse.svg')).href;
const html = readFileSync(resolve(here, 'og-card.html'), 'utf8')
  .replace('FONT_AR_700', font('ibm-plex-sans-arabic', 'ibm-plex-sans-arabic-arabic-700-normal.woff2'))
  .replace('FONT_AR_500', font('ibm-plex-sans-arabic', 'ibm-plex-sans-arabic-arabic-500-normal.woff2'))
  .replace('FONT_EN_600', font('ibm-plex-sans', 'ibm-plex-sans-latin-600-normal.woff2'))
  .replace('FONT_EN_400', font('ibm-plex-sans', 'ibm-plex-sans-latin-400-normal.woff2'))
  .replace('LOGO', logo);
const page = resolve(here, '.og-card.rendered.html');
writeFileSync(page, html);

const browser = await chromium.launch();
const tab = await browser.newPage({ viewport: { width: 1200, height: 630 } });
await tab.goto(pathToFileURL(page).href);
await tab.evaluate(() => document.fonts.ready);
await tab.screenshot({ path: resolve(web, 'public/og-card.png') });
await browser.close();
console.log('public/og-card.png written');
