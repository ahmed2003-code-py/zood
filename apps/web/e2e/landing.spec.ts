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

    // On a phone, sign-in lives in the menu.
    await page.getByRole('banner').getByRole('button', { name: 'القائمة' }).click();
    await page.getByRole('dialog', { name: 'القائمة' }).getByRole('link', { name: 'دخول' }).click();
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

    // The language button's name contains its visible text (WCAG 2.5.3).
    await page.getByRole('banner').getByRole('button', { name: 'Language: العربية' }).click();
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

  test('reduced motion: the finished fill-up is shown, nothing moves, nothing is hidden', async ({ page, context }) => {
    await freshContext(context, 'en');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await openStyled(page, '/');
    await expect(page.locator('[data-printed]')).toHaveAttribute('data-printed', 'true');
    await expect(page.locator('.landing')).toHaveAttribute('data-motion', 'off');
    const animated = await page.evaluate(() => document.getAnimations().filter((animation) => animation.playState === 'running').length);
    expect(animated).toBe(0);
    // Reveals are off: every section is visible without scrolling to it.
    const hidden = await page.evaluate(() => [...document.querySelectorAll('[data-reveal]')].filter((element) => getComputedStyle(element).opacity !== '1').length);
    expect(hidden).toBe(0);
    // The theme switch is instant: no cross-fade.
    await page.getByRole('banner').getByRole('button', { name: 'Switch to dark mode' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await expect(page.locator('html')).not.toHaveAttribute('data-public-theme-fade', '');
  });

  test('mobile menu: opens full screen, traps focus, Escape closes it and focus returns', async ({ page, context }) => {
    await freshContext(context, 'en');
    await page.setViewportSize({ width: 390, height: 844 });
    await openStyled(page, '/');
    const trigger = page.getByRole('banner').getByRole('button', { name: 'Menu' });
    // While the menu is open the page behind is hidden from assistive tech, so check the button by its markup.
    const button = page.locator('header button[aria-haspopup="dialog"]');
    await expect(trigger).toHaveAttribute('aria-expanded', 'false');
    await trigger.click();
    const menu = page.getByRole('dialog', { name: 'Menu' });
    await expect(menu).toBeVisible();
    await expect(button).toHaveAttribute('aria-expanded', 'true');
    // The page behind does not scroll while the menu is open.
    expect(await page.evaluate(() => getComputedStyle(document.body).overflow)).toBe('hidden');
    // Focus stays inside the menu.
    for (let step = 0; step < 14; step++) {
      await page.keyboard.press('Tab');
      expect(await menu.evaluate((element) => element.contains(document.activeElement))).toBe(true);
    }
    // Large targets.
    const link = menu.getByRole('link', { name: 'Pricing' });
    expect((await link.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(48);

    await page.keyboard.press('Escape');
    await expect(menu).toBeHidden();
    await expect(trigger).toHaveAttribute('aria-expanded', 'false');
    await expect(trigger).toBeFocused();

    // A section link closes the menu and takes the visitor there.
    await trigger.click();
    await page.getByRole('dialog', { name: 'Menu' }).getByRole('link', { name: 'Pricing' }).click();
    await expect(page.getByRole('dialog', { name: 'Menu' })).toBeHidden();
    await expect(page.getByRole('heading', { level: 2, name: 'Plans sized to your fleet' })).toBeInViewport();
    await expect(page.locator('#pricing')).toBeFocused();
  });

  test('desktop header: full width, marks the section in view, and anchors land below it', async ({ page, context }) => {
    await freshContext(context, 'en');
    await page.setViewportSize({ width: 1440, height: 900 });
    await openStyled(page, '/');
    const header = page.getByRole('banner');
    const box = await header.boundingBox();
    expect(box?.x).toBe(0);
    expect(box?.width).toBe(1440);
    await header.getByRole('link', { name: 'Security' }).click();
    await expect(header.getByRole('link', { name: 'Security' })).toHaveAttribute('aria-current', 'true');
    // The section heading is not hidden under the fixed header.
    await expect.poll(async () => (await page.locator('#trust-title').boundingBox())?.y ?? 0).toBeGreaterThan((box?.height ?? 0) - 1);
    await expect(header).toHaveAttribute('data-solid', 'true');
  });

  test('scroll reveals never leave blank areas after a jump', async ({ page, context }) => {
    await freshContext(context, 'en');
    await page.setViewportSize({ width: 1280, height: 800 });
    await openStyled(page, '/#faq');
    await page.waitForTimeout(1500);
    const blank = await page.evaluate(() =>
      [...document.querySelectorAll<HTMLElement>('[data-reveal]')].filter((element) => {
        const top = element.getBoundingClientRect().top;
        return top < window.innerHeight && !element.hasAttribute('data-revealed');
      }).length,
    );
    expect(blank).toBe(0);
  });

  test('footer: large targets, legal pages, and the investor demo only in demo mode', async ({ page, context }) => {
    await freshContext(context, 'en');
    await page.setViewportSize({ width: 390, height: 844 });
    await openStyled(page, '/');
    const footer = page.getByRole('contentinfo');
    for (const link of await footer.getByRole('link').all()) {
      expect((await link.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(24);
    }
    // This suite runs the demo build (mock API), where the investor demo exists.
    await expect(footer.getByRole('link', { name: 'Investor demo' })).toBeVisible();
    await footer.getByRole('link', { name: 'Privacy' }).click();
    await expect(page).toHaveURL(/\/privacy$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Privacy policy' })).toBeVisible();
    await expect(page.getByText('Law No. 151 of 2020', { exact: false })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  });

  test('legal pages in Arabic: terms, with placeholders marked for the owner', async ({ page, context }) => {
    await freshContext(context, 'ar');
    await openStyled(page, '/terms');
    await expect(page.getByRole('heading', { level: 1, name: 'شروط الاستخدام' })).toBeVisible();
    await expect(page.locator('mark').first()).toContainText('[');
    await page.getByRole('link', { name: 'اقرأ سياسة الخصوصية' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'سياسة الخصوصية' })).toBeVisible();
  });

  test('demo form: the consent line links to the privacy policy', async ({ page, context }) => {
    await freshContext(context, 'en');
    await openStyled(page, '/#demo');
    await expect(page.locator('#demo form').getByRole('link', { name: 'Privacy policy' })).toHaveAttribute('href', '/privacy');
  });

  test('crawler files are served as files, not as the app', async ({ request }) => {
    const robots = await request.get('/robots.txt');
    expect(await robots.text()).toContain('Sitemap:');
    const sitemap = await request.get('/sitemap.xml');
    expect(await sitemap.text()).toContain('<urlset');
  });
});
