import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { openStyled } from '../helpers';
import { installApiStub, STUB_IDS, type StubRole } from './api-stub';
import { installPosStub, POS_TAGS } from './pos-stub';
import { installStationStub } from './station-stub';

/**
 * Review screenshots of UI pass 2 (not baselines): the company and Ops screens (in <folder>/390 and
 * <folder>/1440), and the station portal and the POS flow (in <folder>), in Arabic and English,
 * Light and Dark. Runs only when PASS2_SHOTS names a folder:
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

// ------------------------------------------------------------------ station portal and POS
const STATION_NOW = '2026-11-18T10:00:00.000Z';
const COMBOS = [
  { lang: 'ar', theme: 'light' },
  { lang: 'en', theme: 'dark' },
  { lang: 'en', theme: 'light' },
  { lang: 'ar', theme: 'dark' },
] as const;
const STATION_SCREENS = [
  ['home', '/station'],
  ['fuelings', '/station/transactions'],
  ['network', '/station/devices'],
  ['shifts', '/station/shifts'],
  ['finance', '/station/settlements'],
] as const;

const label = (lang: string) => (lang === 'ar' ? { scan: 'مسح المركبة', uid: 'رقم الشريحة', next: 'متابعة', fuel: 'إدخال الكمية', review: 'مراجعة', record: 'تأكيد وتسجيل' } : { scan: 'Scan vehicle', uid: 'Tag UID', next: 'Continue', fuel: 'Enter fuel', review: 'Review', record: 'Confirm and record' });

async function shot(page: Page, name: string) {
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(450);
  await page.screenshot({ path: join(OUT!, `${name}.png`), fullPage: name.startsWith('station') });
}

for (const combo of COMBOS) {
  for (const width of [390, 1440]) {
    test(`station ${combo.lang}-${combo.theme} @${width}`, async ({ page, context, baseURL }) => {
      mkdirSync(OUT!, { recursive: true });
      await page.clock.setFixedTime(new Date(STATION_NOW));
      await page.setViewportSize({ width, height: width < 600 ? 844 : 900 });
      await context.addInitScript(({ lang, theme }) => {
        localStorage.setItem('zood.locale', lang);
        localStorage.setItem('zood.theme', theme);
      }, combo);
      await installApiStub(context, 'station', baseURL ?? '');
      await installStationStub(context);
      for (const [name, path] of STATION_SCREENS) {
        await openStyled(page, path);
        await expect(page.locator('main h1')).toBeVisible();
        await shot(page, `station-${name}-${combo.lang}-${combo.theme}-${width}`);
      }
    });

    test(`pos ${combo.lang}-${combo.theme} @${width}`, async ({ page, context, baseURL }) => {
      mkdirSync(OUT!, { recursive: true });
      const l = label(combo.lang);
      await page.clock.setFixedTime(new Date(STATION_NOW));
      await page.setViewportSize({ width, height: width < 600 ? 780 : 900 });
      await context.addInitScript(({ lang, theme }) => {
        localStorage.setItem('zood.locale', lang);
        localStorage.setItem('zood.pos.theme', theme);
        localStorage.setItem('zawed.pos.sim.tags', JSON.stringify({ v: 1, tags: [{ uid: '04A1B2C3D4', lastUsedAt: '2026-11-18T09:00:00Z' }, { uid: '04C0FFEE00', lastUsedAt: '2026-11-18T08:00:00Z' }] }));
      }, combo);
      await installApiStub(context, 'station', baseURL ?? '');
      await installPosStub(context, { now: STATION_NOW });
      await openStyled(page, '/pos');
      const name = (step: string) => `pos-${step}-${combo.lang}-${combo.theme}-${width}`;
      await shot(page, name('01-activate'));
      await page.locator('input').nth(0).fill('POS-201');
      await page.locator('input').nth(1).fill('ABCD-1234');
      await page.locator('[data-step] button').last().click();
      await page.locator('input').first().fill('W-100');
      await shot(page, name('02-sign-in'));
      for (const digit of '135790') await page.getByRole('button', { name: digit, exact: true }).click();
      await expect(page.getByRole('button', { name: l.scan })).toBeVisible();
      await shot(page, name('03-home'));
      await page.getByRole('button', { name: l.scan }).click();
      await shot(page, name('04-tag'));
      await page.getByLabel(l.uid).fill(POS_TAGS.ok);
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await expect(page.getByRole('button', { name: l.fuel })).toBeVisible();
      await shot(page, name('05-verify'));
      await page.getByRole('button', { name: l.fuel }).click();
      for (const digit of '25') await page.getByRole('button', { name: digit, exact: true }).click();
      await shot(page, name('06-volume'));
      await page.getByRole('button', { name: l.review }).click();
      await shot(page, name('07-confirm'));
      await page.getByRole('dialog').getByRole('button', { name: l.record }).click();
      await expect(page.locator('[data-step="done"]')).toBeVisible();
      await shot(page, name('08-done'));
      await page.locator('[data-step="done"] button').first().click();
      await page.waitForTimeout(1600);
      await shot(page, name('09-receipt'));
      await page.locator('[data-step="done"] button').last().click();
      await page.locator('footer button').first().click();
      await shot(page, name('10-history'));
      await page.locator('[data-step="history"] button').last().click();
      await shot(page, name('11-summary'));
      await page.locator('[data-step="summary"] button').last().click();
      await page.getByRole('button', { name: l.scan }).click();
      await page.getByLabel(l.uid).fill(POS_TAGS.cooldown);
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await expect(page.locator('[data-step="declined"]')).toBeVisible();
      await shot(page, name('12-declined-cooldown'));
      await page.locator('header button').last().click();
      await shot(page, name('13-menu'));
    });
  }
}
