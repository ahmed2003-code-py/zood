import { expect, test, type Page } from '@playwright/test';
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

/** A signed-in session as the API returns it (sample user). */
const session = (role = 'FLEET_MANAGER', mustChangePassword = false) => ({
  status: 'AUTHENTICATED',
  session: {
    accessToken: 'test-token',
    expiresInSeconds: 900,
    user: {
      id: '00000000-0000-0000-0000-000000000001',
      email: 'mona@example.com',
      fullName: 'Mona Adel',
      role,
      mustChangePassword,
      mfaEnabled: false,
      scope: { type: 'COMPANY', id: '00000000-0000-0000-0000-000000000002' },
    },
  },
});

/** Answers POST /auth/login (and /auth/mfa/verify) with the given bodies, after a short delay. */
async function stubAuth(page: Page, login: { status: number; body: unknown }, verify?: { status: number; body: unknown }) {
  await page.route('**/api/v1/auth/login', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 400));
    await route.fulfill({ status: login.status, contentType: 'application/json', body: JSON.stringify(login.body) });
  });
  if (verify) {
    await page.route('**/api/v1/auth/mfa/verify', (route) =>
      route.fulfill({ status: verify.status, contentType: 'application/json', body: JSON.stringify(verify.body) }),
    );
  }
}

async function signIn(page: Page, submit = 'Sign in') {
  await page.getByLabel('Email').fill('mona@example.com');
  await page.getByLabel('Password', { exact: true }).fill('a long enough password');
  await page.getByRole('button', { name: submit }).click();
}

test.describe('sign-in page', () => {
  test('one centred card on the moving backdrop; one sign-in for every role', async ({ context, page }) => {
    await freshContext(context, 'en');
    await page.setViewportSize({ width: 1440, height: 900 });
    await openStyled(page, '/login?next=%2Fcompany');
    const heading = page.getByRole('heading', { level: 1, name: 'Sign in to ZAWED' });
    await expect(heading).toBeVisible();
    await expect(page.getByText('One sign-in for fleet companies, partner stations and the ZAWED team', { exact: false })).toBeVisible();
    const card = await page.locator('.signin-card').boundingBox();
    expect(Math.abs((card?.x ?? 0) + (card?.width ?? 0) / 2 - 720)).toBeLessThan(4);
    await expect(page.locator('.fuel-backdrop')).toBeAttached();
    // Header: logo, language, theme; then the form.
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    await expect(page.getByLabel('Email')).toBeFocused();
  });

  test('signed in: the drop fills, a welcome with the first name, then the portal opens', async ({ context, page }) => {
    await freshContext(context, 'en');
    await stubAuth(page, { status: 200, body: session() });
    await openStyled(page, '/login?next=%2Fcompany%2Fwallet');
    await signIn(page);
    // While the request runs the button stays the brand colour and says what is happening.
    const busy = page.getByRole('button', { name: 'Signing in…' });
    await expect(busy).toHaveAttribute('aria-busy', 'true');
    await expect(page.locator('.fuel-drop')).toHaveAttribute('data-state', 'working');

    // The portal starts loading as soon as the sign-in succeeds, while the welcome plays.
    const portalChunk = page.waitForRequest(/CompanyApp-.*\.js$/, { timeout: 1_500 }).catch(() => null);
    const welcome = page.getByRole('status').filter({ hasText: 'Welcome, Mona' });
    await expect(welcome).toBeVisible();
    await expect(page.locator('.fuel-drop')).toHaveAttribute('data-state', 'success');
    await expect(page.locator('.signin-page')).toHaveAttribute('data-state', 'success');
    // (Already cached chunks make no request; then the portal simply opens sooner.)
    await portalChunk;
    // The moment lasts SUCCESS_MS (950 ms); the rest is the portal's own loading time.
    await page.waitForURL('**/company/wallet', { timeout: 6_000, waitUntil: 'commit' });
  });

  test('refused: the error shows at once, the card shakes and the drop drains', async ({ context, page }) => {
    await freshContext(context, 'en');
    await stubAuth(page, { status: 401, body: { code: 'INVALID_CREDENTIALS', message: 'Bad credentials' } });
    await openStyled(page, '/login');
    await signIn(page);
    await expect(page.getByRole('alert')).toHaveText('The email or password is not correct.');
    await expect(page.locator('.signin-card')).toHaveAttribute('data-shake', 'a');
    await expect(page.locator('.fuel-drop')).toHaveAttribute('data-state', 'error');
    await expect(page).toHaveURL(/\/login$/);
    // A second refusal shakes again.
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.locator('.signin-card')).toHaveAttribute('data-shake', 'b');
  });

  test('second factor keeps the same card, and success still welcomes', async ({ context, page }) => {
    await freshContext(context, 'ar');
    await stubAuth(page, { status: 200, body: { status: 'MFA_REQUIRED', challengeToken: 'challenge', expiresInSeconds: 300 } }, { status: 200, body: session('OPS') });
    await openStyled(page, '/login');
    await page.getByLabel('البريد الإلكتروني').fill('mona@example.com');
    await page.getByLabel('كلمة المرور', { exact: true }).fill('a long enough password');
    await page.getByRole('button', { name: 'تسجيل الدخول' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'أدخل رمز التحقق' })).toBeVisible();
    await expect(page.locator('.fuel-drop')).toBeVisible();
    await page.getByRole('textbox', { name: 'رمز التحقق' }).fill('123456');
    await page.getByRole('button', { name: 'تحقق' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'أهلًا بك يا Mona' })).toBeVisible();
    await page.waitForURL('**/ops', { timeout: 6_000, waitUntil: 'commit' });
  });

  test('phone (360 px, Arabic): the card fits, no sideways scrolling, large targets', async ({ context, page }) => {
    await freshContext(context, 'ar');
    await page.setViewportSize({ width: 360, height: 760 });
    await openStyled(page, '/login');
    await expect(page.getByRole('heading', { level: 1, name: 'تسجيل الدخول إلى زوّد' })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
    const submit = await page.getByRole('button', { name: 'تسجيل الدخول' }).boundingBox();
    expect(submit?.height ?? 0).toBeGreaterThanOrEqual(44);
    // The language button names its visible text.
    await expect(page.getByRole('button', { name: /English/ })).toBeVisible();
  });

  test('reduced motion: nothing moves, and a successful sign-in opens the portal straight away', async ({ context, page }) => {
    await freshContext(context, 'en');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await stubAuth(page, { status: 200, body: session('STATION_MANAGER') });
    await openStyled(page, '/login');
    await page.waitForTimeout(300);
    const running = await page.evaluate(() => document.getAnimations().filter((animation) => animation.playState === 'running').length);
    expect(running).toBe(0);
    await signIn(page);
    await page.waitForURL('**/station', { timeout: 6_000, waitUntil: 'commit' });
    // No welcome under reduced motion: the portal opens as soon as it has loaded.
    await expect(page.getByText('Welcome, Mona')).toHaveCount(0);
  });
});
