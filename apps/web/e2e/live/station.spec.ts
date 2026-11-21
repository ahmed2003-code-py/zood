import { expect, test, type BrowserContext, type Page } from '@playwright/test';
import { openStyled } from '../helpers';
import { installApiStub } from './api-stub';
import { installStationStub } from './station-stub';

/**
 * The station portal on live data (VITE_API_MODE=http) with station fixtures: what each screen must
 * say, that no screen scrolls sideways from 360 to 1440 px, and visual baselines in Arabic Light
 * and English Dark at 390 and 1440 px. Run with playwright.live.config.ts.
 */
const NOW = new Date('2026-11-18T12:00:00+02:00');
const SCREENS = [
  ['home', '/station'],
  ['fuelings', '/station/transactions'],
  ['network', '/station/devices'],
  ['shifts', '/station/shifts'],
  ['settlements', '/station/settlements'],
] as const;

async function open(page: Page, context: BrowserContext, baseURL: string | undefined, path: string, lang: 'ar' | 'en' = 'ar', theme: 'light' | 'dark' = 'light') {
  await page.clock.setFixedTime(NOW);
  await context.addInitScript(
    ({ lang, theme }) => {
      localStorage.setItem('zood.locale', lang);
      localStorage.setItem('zood.theme', theme);
    },
    { lang, theme },
  );
  await installApiStub(context, 'station', baseURL ?? '');
  await installStationStub(context);
  await openStyled(page, path);
  await expect(page.locator('main h1')).toBeVisible();
}

test('home: today per station, money, held fuelings with reasons, attention and who is on shift', async ({ page, context, baseURL }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page, context, baseURL, '/station', 'en');
  await expect(page.getByRole('heading', { name: 'Today at your stations' })).toBeVisible();
  await expect(page.getByText('Fifth Settlement Station').first()).toBeVisible();
  await expect(page.getByText('Available for the next payout')).toBeVisible();
  await expect(page.getByText('Sent more than 7 days after the fueling')).toBeVisible();
  await expect(page.getByText('Diesel lane: activation code expired')).toBeVisible();
  await expect(page.getByText('عمر جمال is locked out')).toBeVisible();
  await expect(page.getByRole('main').getByText('محمود سعيد')).toBeVisible();
});

test('network: label before code, station, expired code and locked agent', async ({ page, context, baseURL }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page, context, baseURL, '/station/devices', 'en');
  await expect(page.getByText('Code expired, ask Operations')).toBeVisible();
  await expect(page.getByText(/^Locked until/)).toBeVisible();
  await expect(page.getByText('Must choose a PIN')).toBeVisible();
  await page.getByRole('radio', { name: 'Needs attention' }).click();
  await expect(page.getByText('Spare terminal')).toBeHidden();
});

test('shifts: totals per shift and a drill-down with its fuelings', async ({ page, context, baseURL }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page, context, baseURL, '/station/shifts', 'en');
  const row = page.getByRole('row').filter({ hasText: 'Open' });
  await expect(row).toContainText('EGP');
  await row.click();
  const sheet = page.getByRole('dialog');
  await expect(sheet.getByText('Fuelings in this shift')).toBeVisible();
  await expect(sheet.getByRole('button', { name: 'Open in fuelings' })).toBeVisible();
});

test('fuelings: an end before the start is explained, not sent', async ({ page, context, baseURL }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page, context, baseURL, '/station/transactions?from=2026-11-10&to=2026-11-01', 'en');
  await expect(page.getByText('The start is after the end.').first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Export CSV' })).toBeDisabled();
});

test('settlements: a bank change goes to Operations as a ticket, never with an account number', async ({ page, context, baseURL }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page, context, baseURL, '/station/settlements', 'en');
  await page.getByRole('button', { name: 'Request a change' }).click();
  const sheet = page.getByRole('dialog');
  await expect(sheet.getByLabel('Describe what you need')).toHaveValue(/ends in 4410/);
  await sheet.getByLabel('Describe what you need').fill('New IBAN EG38 0019 0005 0000 0000 2631 8000 2');
  await expect(sheet.getByText('Remove the account number or IBAN; Operations takes it from the bank letter.')).toBeVisible();
});

test('phones: filters fold into a sheet', async ({ page, context, baseURL }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await open(page, context, baseURL, '/station/shifts', 'ar');
  await page.getByRole('button', { name: 'التصفية' }).click();
  await expect(page.getByRole('dialog').getByLabel('المحطة')).toBeVisible();
});

for (const width of [360, 768, 1024]) {
  test(`no sideways scroll on any station screen @${width}`, async ({ page, context, baseURL }) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width, height: 800 });
    await open(page, context, baseURL, '/station', 'ar');
    for (const [, path] of SCREENS) {
      await openStyled(page, path);
      await expect(page.locator('main h1')).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
    }
  });
}

for (const [name, path] of SCREENS.filter(([screen]) => screen !== 'fuelings')) {
  for (const combo of [
    { lang: 'ar', theme: 'light' },
    { lang: 'en', theme: 'dark' },
  ] as const) {
    for (const width of [390, 1440]) {
      test(`station-${name} ${combo.lang}-${combo.theme} @${width}`, async ({ page, context, baseURL }) => {
        await page.setViewportSize({ width, height: width < 600 ? 844 : 900 });
        await open(page, context, baseURL, path, combo.lang, combo.theme);
        await page.waitForLoadState('networkidle');
        await page.evaluate(() => document.fonts.ready);
        await expect(page).toHaveScreenshot(`station-${name}-${combo.lang}-${combo.theme}-${width}.png`);
      });
    }
  }
}
