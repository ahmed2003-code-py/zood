import { expect, test, type Page } from '@playwright/test';

/**
 * Review screenshots for phase sign-off. Skipped unless CAPTURE=1:
 *   CAPTURE=1 npx playwright test e2e/screens.spec.ts
 * Output: docs/screenshots/<phase>/
 */
const PHASE = process.env.PHASE ?? 'd1';
const OUT = `../../docs/screenshots/${PHASE}`;

interface Shot {
  name: string;
  path: string;
  lang: 'ar' | 'en';
  theme: 'light' | 'dark';
  width: number;
  height?: number;
  fullPage?: boolean;
}

const SHOTS: Shot[] = [
  { name: 'demo-ar-light-390', path: '/demo', lang: 'ar', theme: 'light', width: 390, fullPage: true },
  { name: 'demo-en-dark-1440', path: '/demo', lang: 'en', theme: 'dark', width: 1440 },
  { name: 'company-ar-light-390', path: '/company', lang: 'ar', theme: 'light', width: 390, fullPage: true },
  { name: 'company-more-ar-light-390', path: '/company/more', lang: 'ar', theme: 'light', width: 390, fullPage: true },
  { name: 'company-wallet-ar-light-390', path: '/company/wallet', lang: 'ar', theme: 'light', width: 390 },
  { name: 'company-ar-dark-1440', path: '/company', lang: 'ar', theme: 'dark', width: 1440 },
  { name: 'company-en-light-390', path: '/company', lang: 'en', theme: 'light', width: 390, fullPage: true },
  { name: 'company-en-dark-1440', path: '/company', lang: 'en', theme: 'dark', width: 1440 },
  { name: 'company-transactions-en-dark-1440', path: '/company/transactions', lang: 'en', theme: 'dark', width: 1440 },
  { name: 'company-ar-light-768', path: '/company/budgets', lang: 'ar', theme: 'light', width: 768 },
  { name: 'station-ar-light-390', path: '/station', lang: 'ar', theme: 'light', width: 390, fullPage: true },
  { name: 'station-ar-light-1440', path: '/station', lang: 'ar', theme: 'light', width: 1440 },
  { name: 'station-en-dark-1440', path: '/station/devices', lang: 'en', theme: 'dark', width: 1440 },
  { name: 'ops-ar-light-390', path: '/ops', lang: 'ar', theme: 'light', width: 390, fullPage: true },
  { name: 'ops-en-dark-1440', path: '/ops', lang: 'en', theme: 'dark', width: 1440 },
  { name: 'ops-companies-en-dark-1440', path: '/ops/companies', lang: 'en', theme: 'dark', width: 1440 },
  { name: 'pos-ar-light-390', path: '/pos', lang: 'ar', theme: 'light', width: 390 },
  { name: 'pos-ar-light-1440', path: '/pos', lang: 'ar', theme: 'light', width: 1440 },
  { name: 'pos-en-dark-390', path: '/pos', lang: 'en', theme: 'dark', width: 390 },
];

/** The local machine sometimes refuses requests (docs/architecture/known-issues.md); never keep an unstyled shot. */
async function expectStyled(page: Page) {
  const background = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  expect(background, 'stylesheet not applied').not.toBe('rgba(0, 0, 0, 0)');
}

async function prepare(page: Page, shot: Shot) {
  await page.setViewportSize({ width: shot.width, height: shot.height ?? 900 });
  // Persisted preferences (the POS has its own theme key).
  await page.addInitScript(
    ({ lang, theme }) => {
      localStorage.setItem('zood.locale', lang);
      localStorage.setItem('zood.theme', theme);
      localStorage.setItem('zood.pos.theme', theme);
    },
    { lang: shot.lang, theme: shot.theme },
  );
}

for (const shot of SHOTS) {
  test(`capture ${shot.name}`, async ({ page }) => {
    test.skip(!process.env.CAPTURE, 'Set CAPTURE=1 to capture review screenshots');
    await prepare(page, shot);
    await page.goto(shot.path);
    await page.waitForLoadState('networkidle');
    await page.evaluate(() => document.fonts.ready);
    await expectStyled(page);
    await page.screenshot({ path: `${OUT}/${shot.name}.png`, fullPage: shot.fullPage ?? false });
  });
}

test('capture presenter panel', async ({ page }) => {
  test.skip(!process.env.CAPTURE, 'Set CAPTURE=1 to capture review screenshots');
  await prepare(page, { name: 'presenter', path: '/pos', lang: 'ar', theme: 'light', width: 1440 });
  await page.goto('/pos?presenter=1');
  await page.waitForLoadState('networkidle');
  await page.getByRole('complementary').waitFor();
  await expectStyled(page);
  await page.screenshot({ path: `${OUT}/presenter-pos-ar-light-1440.png` });
});
