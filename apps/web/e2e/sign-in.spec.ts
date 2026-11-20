import { expect, test } from '@playwright/test';
import { freshContext } from './helpers';

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
