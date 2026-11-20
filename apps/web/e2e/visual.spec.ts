import { expect, test, type Page } from '@playwright/test';
import { openStyled } from './helpers';

/**
 * Visual baselines for the demo's key screens, so a U1 fix stays fixed. Each screen is checked in
 * Arabic Light and English Dark at a phone and a desktop width; the dashboard in all four
 * combinations. The clock is fixed, so the demo data and every relative time are the same each run.
 *
 * After an intended visual change: npx playwright test e2e/visual.spec.ts --update-snapshots
 */
const NOW = new Date('2026-11-18T10:30:00+02:00');

type Combo = { lang: 'ar' | 'en'; theme: 'light' | 'dark' };
const AR_LIGHT: Combo = { lang: 'ar', theme: 'light' };
const EN_DARK: Combo = { lang: 'en', theme: 'dark' };
const ALL: Combo[] = [AR_LIGHT, { lang: 'ar', theme: 'dark' }, { lang: 'en', theme: 'light' }, EN_DARK];

const SCREENS: Array<{ name: string; path: string; combos: Combo[] }> = [
  { name: 'company-dashboard', path: '/company', combos: ALL },
  { name: 'company-wallet', path: '/company/wallet', combos: [AR_LIGHT, EN_DARK] },
  { name: 'company-transactions', path: '/company/transactions', combos: [AR_LIGHT, EN_DARK] },
  { name: 'ops-companies', path: '/ops/companies', combos: [AR_LIGHT, EN_DARK] },
  { name: 'station-home', path: '/station', combos: [AR_LIGHT, EN_DARK] },
];

async function prepare(page: Page, { lang, theme }: Combo, width: number) {
  await page.clock.setFixedTime(NOW);
  await page.setViewportSize({ width, height: width < 600 ? 844 : 900 });
  await page.addInitScript(
    ({ lang, theme }) => {
      if (sessionStorage.getItem('zood.visual.init')) return;
      localStorage.clear();
      localStorage.setItem('zood.locale', lang);
      localStorage.setItem('zood.theme', theme);
      sessionStorage.setItem('zood.visual.init', '1');
    },
    { lang, theme },
  );
}

for (const screen of SCREENS) {
  for (const combo of screen.combos) {
    for (const width of [390, 1440]) {
      test(`${screen.name} ${combo.lang}-${combo.theme} @${width}`, async ({ page }) => {
        await prepare(page, combo, width);
        await openStyled(page, screen.path);
        await expect(page.locator('main h1')).toBeVisible();
        await page.evaluate(() => document.fonts.ready);
        await expect(page).toHaveScreenshot(`${screen.name}-${combo.lang}-${combo.theme}-${width}.png`);
      });
    }
  }
}

for (const [combo, width] of [
  [AR_LIGHT, 1440],
  [EN_DARK, 390],
] as const) {
  test(`dialog ${combo.lang}-${combo.theme} @${width}`, async ({ page }) => {
    await prepare(page, combo, width);
    await openStyled(page, `/dev/ui/canvas?lang=${combo.lang}&theme=${combo.theme}`);
    await page.getByRole('button', { name: combo.lang === 'ar' ? 'عرض تفاصيل المعاملة' : 'Open transaction details' }).click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.evaluate(() => document.fonts.ready);
    await expect(page).toHaveScreenshot(`dialog-${combo.lang}-${combo.theme}-${width}.png`);
  });
}
