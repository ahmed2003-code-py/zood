import { expect, test } from '@playwright/test';
import { freshContext, openStyled } from './helpers';

/**
 * One sign-in page for every role (owner, 2026-11-20; ADR 0008 amendment). The Super Admin's former
 * page, /admin/login, only forwards to /login so old bookmarks keep working, `next` included.
 */
for (const { lang, title, formerAdminTitle } of [
  { lang: 'ar' as const, title: 'تسجيل الدخول إلى زوّد', formerAdminTitle: 'إدارة المنصة' },
  { lang: 'en' as const, title: 'Sign in to ZAWED', formerAdminTitle: 'Platform administration' },
]) {
  test(`a bookmark of /admin/login opens the one sign-in page (${lang})`, async ({ context, page }) => {
    await freshContext(context, lang);
    await page.goto('/admin/login?next=%2Fops%2Fcompanies');

    await expect(page).toHaveURL((url) => url.pathname === '/login' && url.search === '?next=%2Fops%2Fcompanies');
    await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible();
    await expect(page.getByText(formerAdminTitle)).toHaveCount(0);
  });
}

test.describe('sign-in page', () => {
  test('desktop: the form first, the brand panel beside it with the fill-up illustration', async ({ context, page }) => {
    await freshContext(context, 'en');
    await page.setViewportSize({ width: 1440, height: 900 });
    await openStyled(page, '/login?next=%2Fcompany');

    const form = page.locator('main');
    const panel = page.getByRole('complementary', { name: 'Every litre, from the tag to the ledger.' });
    await expect(form.getByRole('heading', { level: 1, name: 'Sign in to ZAWED' })).toBeVisible();
    await expect(panel.getByRole('heading', { level: 2 })).toBeVisible();
    await expect(panel.getByText('Checked at the pump')).toBeVisible();
    // Fleets, stations and operations all sign in here.
    await expect(form.getByText('One sign-in for fleet companies, partner stations and the ZAWED team', { exact: false })).toBeVisible();
    // Side by side: the form at the inline start (left in English), the panel at the end.
    const formBox = await form.boundingBox();
    const panelBox = await panel.boundingBox();
    expect(formBox && panelBox && formBox.x < panelBox.x).toBe(true);
    // Keyboard users reach the form before anything in the panel.
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    await expect(form.getByLabel('Email')).toBeFocused();
    await expect(page).toHaveURL(/next=%2Fcompany/);
  });

  test('phone (360 px, Arabic): compact band above the form, no sideways scrolling, large targets', async ({ context, page }) => {
    await freshContext(context, 'ar');
    await page.setViewportSize({ width: 360, height: 760 });
    await openStyled(page, '/login');
    await expect(page.getByRole('heading', { level: 1, name: 'تسجيل الدخول إلى زوّد' })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
    const band = await page.getByRole('complementary').boundingBox();
    const title = await page.getByRole('heading', { level: 1 }).boundingBox();
    expect(band && title && band.y < title.y).toBe(true);
    const submit = await page.getByRole('button', { name: 'تسجيل الدخول' }).boundingBox();
    expect(submit?.height ?? 0).toBeGreaterThanOrEqual(44);
    // The language button names its visible text.
    await expect(page.getByRole('button', { name: /English/ })).toBeVisible();
  });

  test('reduced motion: the finished fill-up is shown and nothing moves', async ({ context, page }) => {
    await freshContext(context, 'en');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.setViewportSize({ width: 1440, height: 900 });
    await openStyled(page, '/login');
    await expect(page.locator('[data-printed]')).toHaveAttribute('data-printed', 'true');
    await page.waitForTimeout(300);
    const running = await page.evaluate(() => document.getAnimations().filter((animation) => animation.playState === 'running').length);
    expect(running).toBe(0);
  });
});
