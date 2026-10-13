import { expect, test, type Page } from '@playwright/test';
import { freshContext, openStyled, posFuel, posSignIn, presenter } from './helpers';

/**
 * Review screenshots of interactive flows (skipped unless CAPTURE=1):
 *   CAPTURE=1 PHASE=overnight npx playwright test e2e/screens-flow.spec.ts
 */
const OUT = `../../docs/screenshots/${process.env.PHASE ?? 'overnight'}`;

test.beforeEach(async ({ context }) => {
  test.skip(!process.env.CAPTURE, 'Set CAPTURE=1 to capture review screenshots');
  await freshContext(context);
});

const shot = (page: Page, name: string, fullPage = false) => page.screenshot({ path: `${OUT}/${name}.png`, fullPage });

test('POS online flow', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openStyled(page, '/pos');
  await shot(page, 'pos-01-login');
  await posSignIn(page);
  await shot(page, 'pos-02-home');
  await page.getByRole('button', { name: 'مسح المركبة' }).click();
  await shot(page, 'pos-03-scanning');
  await expect(page.getByText('تم التعرف على المركبة')).toBeVisible();
  await shot(page, 'pos-04-verify');
  await page.getByRole('button', { name: 'إدخال الكمية' }).click();
  for (const digit of '40') await page.getByRole('button', { name: digit, exact: true }).click();
  await shot(page, 'pos-05-fuel-entry');
  await page.getByRole('button', { name: 'مراجعة' }).click();
  await expect(page.getByRole('dialog', { name: 'تأكيد العملية' }).getByRole('button', { name: 'تأكيد' })).toBeEnabled();
  await shot(page, 'pos-06-confirm');
  await page.getByRole('dialog', { name: 'تأكيد العملية' }).getByRole('button', { name: 'تأكيد' }).click();
  await expect(page.getByText('تمت العملية بنجاح')).toBeVisible();
  await shot(page, 'pos-07-success');
  await page.getByRole('button', { name: 'طباعة الإيصال' }).click();
  await expect(page.getByText('تمت طباعة الإيصال')).toBeVisible();
  await page.waitForTimeout(1600);
  await shot(page, 'pos-08-receipt');
  await page.getByRole('button', { name: 'إنهاء' }).click();
  await page.getByRole('button', { name: 'مسح المركبة' }).click();
  await expect(page.getByText('التزود بالوقود غير متاح حاليًا')).toBeVisible();
  await shot(page, 'pos-09-cooldown');
});

test('POS decline, print failure and offline', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openStyled(page, '/pos');
  await posSignIn(page);
  let panel = await presenter(page);
  await panel.getByRole('combobox').selectOption('tag-veh-smr-1274');
  await page.keyboard.press('Control+Shift+P');
  await posFuel(page, '40');
  await expect(page.getByText('تعذر تنفيذ العملية')).toBeVisible();
  await shot(page, 'pos-10-limit-decline-390');
  await page.getByRole('button', { name: 'رجوع للرئيسية' }).click();

  panel = await presenter(page);
  await panel.getByRole('combobox').selectOption('tag-veh-abg-4582');
  await panel.getByRole('radio', { name: 'معطلة' }).click();
  await panel.getByRole('radio', { name: 'غير متصل', exact: true }).click();
  await page.keyboard.press('Control+Shift+P');
  await shot(page, 'pos-11-offline-home-390');
  await posFuel(page, '40');
  await expect(page.getByText('تمت العملية بدون اتصال')).toBeVisible();
  await shot(page, 'pos-12-offline-success-390');
  await page.getByRole('button', { name: 'طباعة الإيصال' }).click();
  await expect(page.getByText('تعذرت الطباعة')).toBeVisible();
  await shot(page, 'pos-13-print-failed-390');
});

test('POS English dark', async ({ page, context }) => {
  await context.addInitScript(() => {
    localStorage.setItem('zood.locale', 'en');
    localStorage.setItem('zood.pos.theme', '"dark"');
    localStorage.setItem('zood.pos.theme', 'dark');
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await openStyled(page, '/pos');
  for (const digit of '123456') await page.getByRole('button', { name: digit, exact: true }).click();
  await page.getByRole('button', { name: 'Scan vehicle' }).click();
  await expect(page.getByText('Vehicle found')).toBeVisible();
  await shot(page, 'pos-14-verify-en-dark-390');
});

test('Company, Station and Operations after a sale', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openStyled(page, '/pos');
  await posSignIn(page);
  await posFuel(page, '40');
  await expect(page.getByText('تمت العملية بنجاح')).toBeVisible();

  await openStyled(page, '/company');
  await shot(page, 'company-home-ar-1440');
  await openStyled(page, '/company/vehicles/veh-abg-4582');
  await shot(page, 'company-vehicle-detail-ar-1440');
  await openStyled(page, '/company/transactions');
  await shot(page, 'company-transactions-ar-1440');
  await page.locator('table tbody tr').first().click();
  await page.getByText('تفاصيل إضافية').click();
  await shot(page, 'company-transaction-detail-ar-1440');
  await openStyled(page, '/station');
  await shot(page, 'station-home-ar-1440');

  const panel = await presenter(page);
  await panel.getByRole('button', { name: 'إطلاق تنبيه سعة الخزان' }).click();
  await expect(page).toHaveURL(/\/ops\/alerts\//);
  await page.keyboard.press('Control+Shift+P');
  await openStyled(page, page.url().replace(/^https?:\/\/[^/]+/, ''));
  await shot(page, 'ops-alert-investigation-ar-1440');
  await openStyled(page, '/ops');
  await shot(page, 'ops-home-ar-1440');
});

test('Company mobile 390', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openStyled(page, '/company');
  await shot(page, 'company-home-ar-390', true);
  await openStyled(page, '/company/transactions');
  await shot(page, 'company-transactions-ar-390');
  await page.getByRole('button', { name: 'تصفية' }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.waitForTimeout(300);
  await shot(page, 'company-filters-sheet-ar-390');
  await page.keyboard.press('Escape');
  await openStyled(page, '/company/vehicles/veh-abg-4582');
  await shot(page, 'company-vehicle-detail-ar-390', true);
});

test('English dark desktop', async ({ page, context }) => {
  await context.addInitScript(() => {
    localStorage.setItem('zood.locale', 'en');
    localStorage.setItem('zood.theme', 'dark');
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  await openStyled(page, '/company');
  await shot(page, 'company-home-en-dark-1440');
  await openStyled(page, '/demo');
  await shot(page, 'demo-en-dark-1440');
});

test('Station and Operations mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openStyled(page, '/station');
  await shot(page, 'station-home-ar-390', true);
  await openStyled(page, '/ops');
  await shot(page, 'ops-home-ar-390', true);
  await openStyled(page, '/station/devices');
  await shot(page, 'station-devices-ar-390');
});

test('Demo launcher mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openStyled(page, '/demo');
  await shot(page, 'demo-ar-light-390', true);
});
