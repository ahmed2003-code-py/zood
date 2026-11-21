import { expect, test, type Page } from '@playwright/test';
import { openStyled } from '../helpers';
import { installApiStub, STUB_IDS, type StubRole } from './api-stub';

/**
 * Visual baselines for the live screens (VITE_API_MODE=http) on the fixed API stub. Run with
 * playwright.live.config.ts. Each screen in Arabic Light and English Dark, at 390 and 1440 px.
 *
 * After an intended visual change:
 *   npx playwright test -c playwright.live.config.ts --update-snapshots
 */
const NOW = new Date('2026-11-18T12:00:00+02:00');

type Combo = { lang: 'ar' | 'en'; theme: 'light' | 'dark' };
const COMBOS: Combo[] = [
  { lang: 'ar', theme: 'light' },
  { lang: 'en', theme: 'dark' },
];

const SCREENS: Array<{ name: string; role: StubRole; path: string; open?: (page: Page) => Promise<void> }> = [
  { name: 'company-home', role: 'company', path: '/company' },
  { name: 'company-budgets', role: 'company', path: '/company/budgets' },
  { name: 'company-limits', role: 'company', path: '/company/limits' },
  { name: 'company-reports', role: 'company', path: '/company/reports' },
  { name: 'company-vehicle', role: 'company', path: `/company/vehicles/${STUB_IDS.vehicle}` },
  { name: 'ops-home', role: 'ops', path: '/ops' },
  { name: 'company-vehicles', role: 'company', path: '/company/vehicles' },
  { name: 'company-wallet', role: 'company', path: '/company/wallet' },
  { name: 'company-fuelings', role: 'company', path: '/company/transactions' },
  { name: 'ops-companies', role: 'ops', path: '/ops/companies' },
  { name: 'ops-company', role: 'ops', path: `/ops/companies/${STUB_IDS.company}` },
  { name: 'ops-topups', role: 'ops', path: '/ops/topups' },
  { name: 'ops-users', role: 'ops', path: '/ops/users' },
  { name: 'ops-station', role: 'ops', path: `/ops/stations/${STUB_IDS.station}` },
  { name: 'station-fuelings', role: 'station', path: '/station/transactions' },
  {
    name: 'ops-edit-company-dialog',
    role: 'ops',
    path: `/ops/companies/${STUB_IDS.company}`,
    open: async (page) => {
      await page.getByRole('main').getByRole('button').filter({ has: page.locator('svg.lucide-pencil') }).first().click();
      await expect(page.getByRole('dialog')).toBeVisible();
    },
  },
];

for (const screen of SCREENS) {
  for (const combo of COMBOS) {
    for (const width of [390, 1440]) {
      test(`${screen.name} ${combo.lang}-${combo.theme} @${width}`, async ({ page, context, baseURL }) => {
        await page.clock.setFixedTime(NOW);
        // KPI figures count up once; reduced motion shows the final value straight away.
        await page.emulateMedia({ reducedMotion: 'reduce' });
        await page.setViewportSize({ width, height: width < 600 ? 844 : 900 });
        await context.addInitScript(({ lang, theme }) => {
          localStorage.setItem('zood.locale', lang);
          localStorage.setItem('zood.theme', theme);
        }, combo);
        await installApiStub(context, screen.role, baseURL ?? '');
        await openStyled(page, screen.path);
        await expect(page.locator('main h1')).toBeVisible();
        // The page never scrolls sideways, at any width.
        expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
        await screen.open?.(page);
        await page.evaluate(() => document.fonts.ready);
        await page.waitForLoadState('networkidle');
        await expect(page).toHaveScreenshot(`${screen.name}-${combo.lang}-${combo.theme}-${width}.png`);
      });
    }
  }
}

test('a form dialog traps focus, closes on Escape and gives focus back', async ({ page, context, baseURL }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await context.addInitScript(() => localStorage.setItem('zood.locale', 'en'));
  await installApiStub(context, 'ops', baseURL ?? '');
  await openStyled(page, '/ops/users');
  const opener = page.getByRole('main').getByRole('button', { name: 'New user' });
  await opener.click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  // Tabbing many times never leaves the dialog.
  for (let step = 0; step < 15; step++) await page.keyboard.press('Tab');
  expect(await dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(opener).toBeFocused();
});
