import { expect, test, type Page } from '@playwright/test';
import { openStyled } from './helpers';

/** Environmental, documented in docs/architecture/known-issues.md; any other console error fails the test. */
const ENVIRONMENTAL = /ERR_NETWORK_ACCESS_DENIED/;

const WIDTHS = [390, 768, 1440] as const;
const COMBINATIONS = [
  { lang: 'ar', dir: 'rtl', theme: 'light' },
  { lang: 'ar', dir: 'rtl', theme: 'dark' },
  { lang: 'en', dir: 'ltr', theme: 'light' },
  { lang: 'en', dir: 'ltr', theme: 'dark' },
] as const;

async function horizontalOverflow(page: Page): Promise<number> {
  return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

for (const width of WIDTHS) {
  for (const combination of COMBINATIONS) {
    test(`canvas ${combination.lang}-${combination.theme} @${width}px`, async ({ page }) => {
      const errors: string[] = [];
      page.on('pageerror', (error) => errors.push(error.message));
      page.on('console', (message) => {
        if (message.type() === 'error' && !ENVIRONMENTAL.test(message.text())) errors.push(message.text());
      });

      await page.setViewportSize({ width, height: 900 });
      await openStyled(page, `/dev/ui/canvas?lang=${combination.lang}&theme=${combination.theme}`);

      const html = page.locator('html');
      await expect(html).toHaveAttribute('lang', combination.lang);
      await expect(html).toHaveAttribute('dir', combination.dir);
      await expect(html).toHaveAttribute('data-theme', combination.theme);

      // Mobile shows the list, desktop shows the table.
      if (width >= 1024) await expect(page.locator('table')).toBeVisible();
      else await expect(page.locator('table')).toBeHidden();

      expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
      expect(errors).toEqual([]);

      await page.screenshot({
        path: `test-results/screens/canvas-${combination.lang}-${combination.theme}-${width}.png`,
        fullPage: true,
      });
    });
  }
}

test('toolbar switches language and theme instantly without a reload', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/dev/ui');
  await page.evaluate(() => localStorage.clear());
  await page.reload();

  // A marker on window survives only if the page is not reloaded.
  await page.evaluate(() => ((window as unknown as { __zoodMarker: boolean }).__zoodMarker = true));

  await page.getByRole('radio', { name: 'English' }).click();
  await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  await expect(page.getByRole('heading', { name: 'Design system' })).toBeVisible();

  await page.getByRole('radio', { name: 'Dark' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

  await page.getByRole('radio', { name: 'العربية' }).click();
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');

  expect(await page.evaluate(() => (window as unknown as { __zoodMarker?: boolean }).__zoodMarker)).toBe(true);

  // The single-view preview frame follows the toolbar (shared preference + storage sync).
  const frame = page.frameLocator('iframe');
  await expect(frame.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(frame.locator('html')).toHaveAttribute('data-theme', 'dark');

  // Preferences persist across sessions.
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('lang', 'ar');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
});

test('the canvas sheet is a bottom sheet on mobile and a side panel on desktop', async ({ page }) => {
  for (const [width, check] of [
    [390, (box: { x: number; y: number; width: number; height: number }) => box.width >= 389] as const,
    [1440, (box: { x: number; y: number; width: number; height: number }) => box.width < 600 && box.x < 10] as const,
  ]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto('/dev/ui/canvas?lang=ar&theme=light');
    await page.getByRole('button', { name: 'عرض تفاصيل المعاملة' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await page.waitForTimeout(300);
    const box = await dialog.boundingBox();
    expect(box && check(box)).toBe(true);
    await page.keyboard.press('Escape');
  }
});
