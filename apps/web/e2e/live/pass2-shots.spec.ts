import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { openStyled } from '../helpers';
import { installApiStub, STUB_IDS, type StubRole } from './api-stub';

/**
 * Review screenshots of the screens UI pass 2 changed (not baselines): every screen at 390 and
 * 1440 px, in Arabic and English, Light and Dark. Runs only when PASS2_SHOTS names a folder:
 *
 *   PASS2_SHOTS=/path/to/folder npx playwright test -c playwright.live.config.ts pass2-shots
 */
const OUT = process.env.PASS2_SHOTS;
test.skip(!OUT, 'set PASS2_SHOTS to a folder to take the review screenshots');

const NOW = new Date('2026-11-18T12:00:00+02:00');

const SCREENS: Array<{ name: string; role: StubRole; path: string; open?: (page: Page) => Promise<void> }> = [
  { name: 'company-home', role: 'company', path: '/company' },
  { name: 'company-budgets', role: 'company', path: '/company/budgets' },
  { name: 'company-limits', role: 'company', path: '/company/limits' },
  { name: 'company-reports', role: 'company', path: '/company/reports' },
  { name: 'company-vehicle', role: 'company', path: `/company/vehicles/${STUB_IDS.vehicle}` },
  { name: 'company-vehicles', role: 'company', path: '/company/vehicles' },
  { name: 'company-wallet', role: 'company', path: '/company/wallet' },
  { name: 'company-fuelings', role: 'company', path: '/company/transactions' },
  { name: 'company-fueling', role: 'company', path: `/company/transactions/${STUB_IDS.fueling}` },
  { name: 'company-tickets-new', role: 'company', path: `/company/tickets?new=1&vehicleId=${STUB_IDS.vehicle}` },
  { name: 'company-ticket', role: 'company', path: `/company/tickets/${STUB_IDS.ticket}` },
  {
    name: 'company-notifications',
    role: 'company',
    path: '/company',
    open: async (page) => {
      await page.getByRole('button', { name: /Notifications|الإشعارات/ }).filter({ visible: true }).first().click();
      await expect(page.getByRole('heading', { name: /Notifications|الإشعارات/ }).first()).toBeVisible();
    },
  },
  { name: 'company-not-found', role: 'company', path: '/company/nowhere' },
  { name: 'ops-home', role: 'ops', path: '/ops' },
  { name: 'ops-topups', role: 'ops', path: '/ops/topups' },
  { name: 'ops-company', role: 'ops', path: `/ops/companies/${STUB_IDS.company}` },
  { name: 'ops-companies', role: 'ops', path: '/ops/companies' },
  { name: 'ops-users', role: 'ops', path: '/ops/users' },
  { name: 'ops-station', role: 'ops', path: `/ops/stations/${STUB_IDS.station}` },
  { name: 'ops-devices', role: 'ops', path: '/ops/devices' },
  { name: 'ops-alerts', role: 'ops', path: '/ops/alerts' },
  { name: 'ops-leads', role: 'ops', path: '/ops/leads' },
  { name: 'ops-tickets', role: 'ops', path: '/ops/tickets' },
  {
    name: 'ops-filters-sheet',
    role: 'ops',
    path: '/ops/users',
    open: async (page) => {
      const button = page.getByRole('button', { name: /^(Filters|الفلاتر)/ }).filter({ visible: true });
      if (await button.count()) {
        await button.first().click();
        await expect(page.getByRole('dialog')).toBeVisible();
      }
    },
  },
];

for (const screen of SCREENS) {
  for (const lang of ['ar', 'en'] as const) {
    for (const theme of ['light', 'dark'] as const) {
      for (const width of [390, 1440]) {
        test(`${screen.name} ${lang}-${theme} @${width}`, async ({ page, context, baseURL }) => {
          await page.clock.setFixedTime(NOW);
          await page.emulateMedia({ reducedMotion: 'reduce' });
          await page.setViewportSize({ width, height: width < 600 ? 844 : 900 });
          await context.addInitScript(
            ({ lang: locale, theme: mode }) => {
              localStorage.setItem('zood.locale', locale);
              localStorage.setItem('zood.theme', mode);
            },
            { lang, theme },
          );
          await installApiStub(context, screen.role, baseURL ?? '');
          await openStyled(page, screen.path);
          await expect(page.locator('main h1')).toBeAttached();
          expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
          await screen.open?.(page);
          await page.evaluate(() => document.fonts.ready);
          await page.waitForLoadState('networkidle');
          const folder = `${OUT}/${width}`;
          mkdirSync(folder, { recursive: true });
          await page.screenshot({ path: `${folder}/${screen.name}-${lang}-${theme}.png`, fullPage: true, animations: 'disabled' });
        });
      }
    }
  }
}
