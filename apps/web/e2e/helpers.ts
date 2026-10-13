import { expect, type BrowserContext, type Page } from '@playwright/test';

/**
 * Opens a route and makes sure the stylesheet applied. The development machine sometimes refuses
 * local requests (docs/architecture/known-issues.md); this reloads instead of letting a layout
 * assertion run on an unstyled page. Logical failures are never retried here.
 */
export async function openStyled(page: Page, path: string) {
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

/** Fresh demo state and Arabic Light preferences for every page in the context. */
export async function freshContext(context: BrowserContext, lang: 'ar' | 'en' = 'ar') {
  await context.addInitScript((locale) => {
    if (!sessionStorage.getItem('zood.e2e.init')) {
      if (!localStorage.getItem('zood.e2e.keep')) {
        localStorage.clear();
        localStorage.setItem('zood.e2e.keep', '1');
      }
      localStorage.setItem('zood.locale', locale);
      localStorage.setItem('zood.theme', 'light');
      sessionStorage.setItem('zood.e2e.init', '1');
    }
  }, lang);
}

export async function posSignIn(page: Page) {
  await expect(page.getByText('دخول العامل')).toBeVisible();
  for (const digit of '123456') await page.getByRole('button', { name: digit, exact: true }).click();
  await expect(page.getByRole('button', { name: 'مسح المركبة' })).toBeVisible();
}

/** Scan → verify → enter liters → confirm. Leaves the POS on the outcome screen. */
export async function posFuel(page: Page, liters: string) {
  await page.getByRole('button', { name: 'مسح المركبة' }).click();
  await expect(page.getByText('تم التعرف على المركبة')).toBeVisible();
  await page.getByRole('button', { name: 'إدخال الكمية' }).click();
  for (const digit of liters) await page.getByRole('button', { name: digit, exact: true }).click();
  await page.getByRole('button', { name: 'مراجعة' }).click();
  const confirm = page.getByRole('dialog', { name: 'تأكيد العملية' }).getByRole('button', { name: 'تأكيد' });
  await expect(confirm).toBeEnabled();
  await confirm.click();
}

/** Raw persisted demo state (for financial assertions). */
export async function demoState(page: Page) {
  return page.evaluate(() => JSON.parse(localStorage.getItem('zood.demo.v1') ?? '{}').state);
}

export async function presenter(page: Page) {
  const panel = page.getByRole('complementary', { name: 'لوحة العرض' });
  if (!(await panel.isVisible())) await page.keyboard.press('Control+Shift+P');
  await expect(panel).toBeVisible();
  return panel;
}
