import { expect, test, type Page } from '@playwright/test';

async function open(page: Page, path: string, width: number, lang: 'ar' | 'en' = 'ar') {
  await page.setViewportSize({ width, height: 900 });
  await page.addInitScript((locale) => {
    if (!sessionStorage.getItem('zood.e2e.init')) {
      localStorage.clear();
      localStorage.setItem('zood.locale', locale);
      localStorage.setItem('zood.theme', 'light');
      sessionStorage.setItem('zood.e2e.init', '1');
    }
  }, lang);
  // The development machine sometimes refuses local requests (docs/architecture/known-issues.md).
  // Reload until the stylesheet is applied so layout assertions never run on an unstyled page.
  for (let attempt = 0; attempt < 4; attempt++) {
    try {
      await (attempt === 0 ? page.goto(path) : page.reload());
      await page.waitForLoadState('networkidle');
      const styled = await page.evaluate(() => getComputedStyle(document.body).backgroundColor !== 'rgba(0, 0, 0, 0)');
      if (styled) return;
    } catch (error) {
      if (attempt === 3) throw error;
    }
  }
  throw new Error(`Stylesheet was not applied for ${path}`);
}

async function horizontalOverflow(page: Page) {
  return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

test.describe('no horizontal scrolling', () => {
  for (const path of ['/demo', '/company', '/company/more', '/company/budgets', '/station', '/ops', '/ops/companies', '/pos']) {
    for (const width of [360, 390, 768]) {
      test(`${path} @${width}px`, async ({ page }) => {
        await open(page, path, width);
        expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
      });
    }
  }
});

test('mobile: bottom navigation, More, and back link to More', async ({ page }) => {
  await open(page, '/company', 390);
  await expect(page.locator('[data-shell="sidebar"]')).toBeHidden();
  const bottomNav = page.locator('[data-shell="bottom-nav"]');
  await expect(bottomNav).toBeVisible();
  await expect(bottomNav.getByRole('link', { name: 'الرئيسية' })).toHaveAttribute('aria-current', 'page');

  await bottomNav.getByRole('link', { name: 'المزيد' }).click();
  await expect(page).toHaveURL(/\/company\/more$/);
  await expect(bottomNav.getByRole('link', { name: 'المزيد' })).toHaveAttribute('aria-current', 'page');

  await page.getByRole('link', { name: 'المحفظة' }).click();
  await expect(page).toHaveURL(/\/company\/wallet$/);
  // Secondary screens keep "More" active and offer a back link to More.
  await expect(bottomNav.getByRole('link', { name: 'المزيد' })).toHaveAttribute('aria-current', 'page');
  await page.getByRole('banner').getByRole('link', { name: 'المزيد' }).click();
  await expect(page).toHaveURL(/\/company\/more$/);

  await bottomNav.getByRole('link', { name: 'المركبات' }).click();
  await expect(page).toHaveURL(/\/company\/vehicles$/);
  await expect(bottomNav.getByRole('link', { name: 'المركبات' })).toHaveAttribute('aria-current', 'page');
});

test('desktop: the sidebar sits on the right in Arabic and on the left in English', async ({ page }) => {
  await open(page, '/company', 1440, 'ar');
  const sidebar = page.locator('[data-shell="sidebar"]');
  await expect(sidebar).toBeVisible();
  await expect(page.locator('[data-shell="bottom-nav"]')).toBeHidden();
  const arBox = await sidebar.boundingBox();
  expect(arBox && arBox.x).toBeGreaterThan(1000);

  await sidebar.getByRole('link', { name: 'المعاملات' }).click();
  await expect(sidebar.getByRole('link', { name: 'المعاملات' })).toHaveAttribute('aria-current', 'page');

  // Switch language from the account menu: direction flips immediately, without a reload.
  await page.evaluate(() => ((window as unknown as { __marker: boolean }).__marker = true));
  await page.getByRole('button', { name: 'قائمة الحساب' }).click();
  await page.getByRole('radio', { name: 'English' }).click();
  await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
  await page.keyboard.press('Escape');
  const enBox = await sidebar.boundingBox();
  expect(enBox && enBox.x).toBeLessThan(10);
  await expect(sidebar.getByRole('link', { name: 'Transactions' })).toHaveAttribute('aria-current', 'page');
  expect(await page.evaluate(() => (window as unknown as { __marker?: boolean }).__marker)).toBe(true);
});

test('desktop: theme changes immediately from the account menu', async ({ page }) => {
  await open(page, '/station', 1440, 'en');
  await page.getByRole('button', { name: 'Account menu' }).click();
  await page.getByRole('radio', { name: 'Dark' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
});

test('tablet: icon rail with a drawer for the full navigation', async ({ page }) => {
  await open(page, '/company', 768);
  await expect(page.locator('[data-shell="rail"]')).toBeVisible();
  await expect(page.locator('[data-shell="sidebar"]')).toBeHidden();
  await expect(page.locator('[data-shell="bottom-nav"]')).toBeHidden();
  await page.getByRole('button', { name: 'فتح قائمة التنقل' }).click();
  const drawer = page.getByRole('dialog');
  await expect(drawer.getByText('الوقود')).toBeVisible();
  await drawer.getByRole('link', { name: 'الميزانيات' }).click();
  await expect(page).toHaveURL(/\/company\/budgets$/);
  await expect(drawer).toBeHidden();
});

test('operations: the mobile Network tab covers stations, devices and sync', async ({ page }) => {
  await open(page, '/ops', 390);
  const bottomNav = page.locator('[data-shell="bottom-nav"]');
  await bottomNav.getByRole('link', { name: 'الشبكة' }).click();
  await expect(page).toHaveURL(/\/ops\/stations$/);
  await page.getByRole('navigation', { name: 'الشبكة' }).getByRole('link', { name: 'المزامنة' }).click();
  await expect(page).toHaveURL(/\/ops\/sync$/);
  await expect(bottomNav.getByRole('link', { name: 'الشبكة' })).toHaveAttribute('aria-current', 'page');
});

test('POS: full-screen on mobile, device frame on desktop', async ({ page }) => {
  await open(page, '/pos', 390);
  const device = page.locator('[data-shell="pos-device"]');
  expect((await device.boundingBox())?.width).toBe(390);
  await expect(page.getByText('دخول العامل')).toBeVisible();

  await page.setViewportSize({ width: 1440, height: 900 });
  const box = await device.boundingBox();
  expect(box && box.width).toBeLessThan(420);
  expect(box && box.x).toBeGreaterThan(400);
});

test('presenter: hidden by default, opens with the shortcut and drives the demo state', async ({ page }) => {
  await open(page, '/pos', 1440);
  const panel = page.getByRole('complementary', { name: 'لوحة العرض' });
  await expect(panel).toHaveCount(0);

  await page.keyboard.press('Control+Shift+P');
  await expect(panel).toBeVisible();

  // Connectivity is shared demo state: the POS shows the offline banner.
  await panel.getByRole('radio', { name: 'غير متصل' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'وضع عدم الاتصال' })).toBeVisible();
  await panel.getByRole('radio', { name: 'متصل', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'وضع عدم الاتصال' })).toHaveCount(0);

  await panel.getByRole('radio', { name: 'الشركة' }).click();
  await expect(page).toHaveURL(/\/company$/);

  await page.keyboard.press('Control+Shift+P');
  await expect(panel).toHaveCount(0);
});

test('presenter: opens with ?presenter=1', async ({ page }) => {
  await open(page, '/company?presenter=1', 1440);
  await expect(page.getByRole('complementary', { name: 'لوحة العرض' })).toBeVisible();
});
