import { expect, test, type BrowserContext, type Page } from '@playwright/test';
import { openStyled } from '../helpers';
import { installApiStub } from './api-stub';
import { installPosStub, POS_TAGS, type PosStubOptions } from './pos-stub';

/**
 * The live POS (VITE_API_MODE=http) on the device-channel stub: activation, sign-in, verify,
 * confirm, receipt, history and the end-of-shift summary; declines, the lockout countdown and the
 * way out of the forced PIN change. Run with playwright.live.config.ts.
 */
const NOW = '2026-11-18T10:00:00.000Z';

async function openPos(page: Page, context: BrowserContext, baseURL: string | undefined, options: Partial<PosStubOptions> = {}, lang: 'ar' | 'en' = 'ar') {
  await page.clock.setFixedTime(new Date(NOW));
  await context.addInitScript((locale) => {
    localStorage.setItem('zood.locale', locale);
  }, lang);
  await installApiStub(context, 'station', baseURL ?? '');
  await installPosStub(context, { now: NOW, ...options });
  await openStyled(page, '/pos');
}

async function activateAndSignIn(page: Page, pin = '135790') {
  await page.getByLabel('كود الجهاز').fill('POS-201');
  await page.getByLabel('كود التفعيل').fill('ABCD-1234');
  await page.getByRole('button', { name: 'تفعيل', exact: true }).click();
  await page.getByLabel('رقم العامل').fill('W-100');
  for (const digit of pin) await page.getByRole('button', { name: digit, exact: true }).click();
}

async function readTag(page: Page, uid: string) {
  await page.getByRole('button', { name: 'مسح المركبة' }).click();
  await page.getByLabel('رقم الشريحة').fill(uid);
  await page.getByRole('button', { name: 'متابعة', exact: true }).click();
}

test.beforeEach(async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
});

test('verify, confirm, record, receipt, history and the end-of-shift summary', async ({ page, context, baseURL }) => {
  await openPos(page, context, baseURL);
  await activateAndSignIn(page);
  await expect(page.getByRole('button', { name: 'مسح المركبة' })).toBeVisible();
  await expect(page.getByTestId('pos-connection')).toContainText('متصل');

  await readTag(page, POS_TAGS.ok);
  // Verify: plate, company, fuel, wallet word (never an amount), the day's limit left, no cooldown.
  await expect(page.getByText('تم التعرف على المركبة')).toBeVisible();
  await expect(page.getByText('النيل للنقل')).toBeVisible();
  await expect(page.getByText('المحفظة: متاحة')).toBeVisible();
  await expect(page.getByText(/متبقٍ اليوم/)).toBeVisible();
  await page.getByRole('button', { name: 'إدخال الكمية' }).click();

  for (const digit of '25') await page.getByRole('button', { name: digit, exact: true }).click();
  await page.getByRole('button', { name: 'مراجعة' }).click();
  // Confirm: litres and the amount before anything is recorded (25 L × 17.25 = 431.25).
  const sheet = page.getByRole('dialog', { name: 'تأكيد العملية' });
  await expect(sheet).toContainText('431.25');
  await sheet.getByRole('button', { name: 'تأكيد وتسجيل' }).click();

  await expect(page.getByRole('status').filter({ hasText: 'تمت العملية بنجاح' })).toBeVisible();
  await page.getByRole('button', { name: 'طباعة الإيصال' }).click();
  await expect(page.getByRole('figure')).toBeVisible();
  await page.getByRole('button', { name: 'إنهاء' }).click();

  await page.getByRole('button', { name: 'سجل الوردية' }).click();
  await expect(page.getByRole('listitem')).toHaveCount(1);
  await page.getByRole('button', { name: 'إنهاء الوردية' }).click();
  await expect(page.getByRole('heading', { name: 'نهاية الوردية' })).toBeVisible();
  await expect(page.getByText('431.25').first()).toBeVisible();
  await page.getByRole('button', { name: 'إنهاء الوردية وتسجيل الخروج' }).click();
  await expect(page.getByText('انتهت الوردية. يمكن للعامل التالي تسجيل الدخول.')).toBeVisible();
});

test('declines before the volume: cooldown with a countdown, and a suspended wallet', async ({ page, context, baseURL }) => {
  await openPos(page, context, baseURL);
  await activateAndSignIn(page);
  await readTag(page, POS_TAGS.cooldown);
  await expect(page.getByRole('alert').filter({ hasText: 'التزود بالوقود غير متاح حاليًا' })).toBeVisible();
  await expect(page.getByText('47:00', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'رجوع للرئيسية' }).click();

  await readTag(page, POS_TAGS.suspended);
  await expect(page.getByRole('alert').filter({ hasText: 'حساب الشركة موقوف' })).toBeVisible();
});

test('a wrong pump fuel is refused before the volume', async ({ page, context, baseURL }) => {
  await openPos(page, context, baseURL);
  await activateAndSignIn(page);
  await readTag(page, POS_TAGS.ok);
  await page.getByRole('button', { name: 'إدخال الكمية' }).click();
  await page.getByRole('button', { name: /وقود الطلمبة: بنزين 92/ }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'سولار' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'وقود غير مناسب' })).toBeVisible();
});

test('a lockout counts down and shows attempts left when the API sends them', async ({ page, context, baseURL }) => {
  await openPos(page, context, baseURL, { lockedFor: 840 });
  await activateAndSignIn(page);
  await expect(page.getByRole('alert').filter({ hasText: 'الدخول مقفل مؤقتًا' })).toContainText('14:00');
});

test('attempts left are shown after a wrong PIN', async ({ page, context, baseURL }) => {
  await openPos(page, context, baseURL, { attemptsLeft: 2 });
  await activateAndSignIn(page);
  await expect(page.getByText('الرقم السري غير صحيح. بقيت محاولتان قبل القفل.')).toBeVisible();
});

test('the forced PIN change always has a way out', async ({ page, context, baseURL }) => {
  await openPos(page, context, baseURL, { mustChangePin: true });
  await activateAndSignIn(page);
  await expect(page.getByRole('heading', { name: 'اختر رقمك السري' })).toBeVisible();
  await page.getByRole('button', { name: 'تسجيل الخروج وإنهاء الوردية' }).click();
  await expect(page.getByRole('heading', { name: 'دخول العامل' })).toBeVisible();
});

test('language and theme switch from the terminal menu', async ({ page, context, baseURL }) => {
  await openPos(page, context, baseURL);
  await page.getByRole('button', { name: 'قائمة العامل' }).click();
  await page.getByRole('radio', { name: 'English' }).click();
  await expect(page.getByRole('heading', { name: 'Activate this terminal' })).toBeVisible();
  await page.getByRole('radio', { name: 'Dark' }).click();
  await expect(page.locator('[data-surface="pos"]')).toHaveAttribute('data-theme', 'dark');
});
