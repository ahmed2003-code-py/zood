import { expect, test, type Page } from '@playwright/test';
import { freshContext, openStyled } from './helpers';

async function horizontalOverflow(page: Page) {
  return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

test.describe('landing page', () => {
  test('Arabic: hero, sign-in entry and no sideways scrolling on a phone', async ({ page, context }) => {
    await freshContext(context, 'ar');
    await page.setViewportSize({ width: 390, height: 844 });
    await openStyled(page, '/');
    await expect(page).toHaveURL(/\/$/);
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('heading', { level: 1 })).toContainText('كل لتر بنزين في أسطولك');
    expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);

    await page.getByRole('banner').getByRole('link', { name: 'دخول' }).click();
    await expect(page).toHaveURL(/\/login$/);
  });

  test('English: sections, FAQ accordion and language switch', async ({ page, context }) => {
    await freshContext(context, 'en');
    await page.setViewportSize({ width: 1440, height: 900 });
    await openStyled(page, '/');
    await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
    await expect(page.getByRole('heading', { level: 1 })).toContainText('Every litre your fleet buys');
    for (const name of ['For fleet owners: you set the rules for each vehicle', 'Plans sized to your fleet', 'Questions people ask']) {
      await expect(page.getByRole('heading', { level: 2, name })).toBeAttached();
    }
    // Prices are placeholders and say so.
    await expect(page.getByText('These prices are indicative.', { exact: false })).toBeVisible();

    const question = page.getByRole('button', { name: 'If the station loses internet, does fueling stop?' });
    await question.click();
    await expect(question).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByText('The POS carries on under the same limits', { exact: false })).toBeVisible();

    await page.getByRole('banner').getByRole('button', { name: 'Change language' }).click();
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('heading', { level: 1 })).toContainText('كل لتر بنزين');
  });

  test('demo form: errors are announced and the first wrong field gets focus', async ({ page, context }) => {
    await freshContext(context, 'en');
    await openStyled(page, '/#demo');
    const form = page.locator('#demo form');
    await form.getByRole('button', { name: 'Send request' }).click();
    await expect(form.getByRole('alert')).toHaveText('Some fields need another look');
    const company = form.getByLabel('Company name');
    await expect(company).toBeFocused();
    await expect(company).toHaveAttribute('aria-invalid', 'true');

    await form.getByLabel('Mobile number').fill('0131234');
    await form.getByLabel('Mobile number').blur();
    await expect(form.getByText('Enter an 11-digit Egyptian mobile', { exact: false })).toBeVisible();
  });

  test('demo form: a pricing tier picks the fleet size, and a request becomes an Ops lead (mock mode)', async ({ page, context }) => {
    await freshContext(context, 'ar');
    await page.setViewportSize({ width: 1440, height: 900 });
    await openStyled(page, '/');

    // "Get a quote" on the 50–99 tier fills the fleet size and moves to the form.
    await page.locator('#pricing li').nth(2).getByRole('button', { name: 'اطلب عرض سعر' }).click();
    const form = page.locator('#demo form');
    await expect(form.getByLabel('عدد العربيات')).toHaveValue('50-99');

    await form.getByLabel('اسم الشركة').fill('شركة الاختبار للنقل');
    await form.getByLabel('اسمك').fill('منى عادل');
    // Arabic digits and the +20 prefix are accepted and normalised.
    await form.getByLabel('رقم الموبايل').fill('+٢٠ ١٠١ ٢٣٤ ٥٦٧٨');
    await form.getByLabel('الإيميل').fill('mona@example.com');
    await form.getByLabel('رقم الموبايل').blur();
    await expect(form.getByLabel('رقم الموبايل')).toHaveValue('01012345678');
    await form.getByRole('button', { name: 'ابعت الطلب' }).click();

    await expect(page.getByRole('heading', { name: /وصلنا طلبك/ })).toBeVisible();
    await expect(page.getByText('01012345678', { exact: false })).toBeVisible();

    await openStyled(page, '/ops/leads');
    await expect(page.getByRole('heading', { level: 1, name: 'طلبات الديمو' })).toBeVisible();
    const row = page.getByRole('row').filter({ hasText: 'شركة الاختبار للنقل' });
    await expect(row).toBeVisible();
    await expect(row).toContainText('جديد');

    // Change its status from the sheet.
    await row.click();
    const sheet = page.getByRole('dialog', { name: 'شركة الاختبار للنقل' });
    await sheet.getByLabel('الحالة').selectOption('CONTACTED');
    await sheet.getByLabel('ملاحظة').fill('اتصلنا، الديمو يوم الأحد');
    await sheet.getByRole('button', { name: 'حفظ' }).click();
    await expect(sheet).toBeHidden();
    // The list shows NEW by default, so the contacted request leaves it.
    await expect(page.getByRole('row').filter({ hasText: 'شركة الاختبار للنقل' })).toHaveCount(0);
  });

  test('reduced motion: the finished fill-up is shown, nothing moves', async ({ page, context }) => {
    await freshContext(context, 'en');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await openStyled(page, '/');
    await expect(page.locator('[data-printed]')).toHaveAttribute('data-printed', 'true');
    await expect(page.locator('.landing')).toHaveAttribute('data-motion', 'off');
    const animated = await page.evaluate(() => document.getAnimations().filter((animation) => animation.playState === 'running').length);
    expect(animated).toBe(0);
  });
});
