import { expect, test } from '@playwright/test';
import { demoState, freshContext, openStyled, posFuel, posSignIn, presenter } from './helpers';

const HERO_PLATE = 'أ ب ج 4582';

test.beforeEach(async ({ context, page }) => {
  await freshContext(context);
  await page.setViewportSize({ width: 1440, height: 900 });
});

test('main flow: POS fuels 40 L and the open Company portal updates live, debiting once', async ({ context, page }) => {
  // Company portal open in another window before the fueling.
  const company = await context.newPage();
  await company.setViewportSize({ width: 1440, height: 900 });
  await openStyled(company, '/company');
  await expect(company.getByText('842,350')).toBeVisible();

  await openStyled(page, '/pos');
  await posSignIn(page);
  await posFuel(page, '40');
  await expect(page.getByText('تمت العملية بنجاح')).toBeVisible();
  await expect(page.getByText('760.00').first()).toBeVisible();

  // Live update without reload: wallet balance and the new transaction appear.
  await expect(company.getByText('841,590')).toBeVisible();
  await expect(company.getByText(HERO_PLATE).first()).toBeVisible();

  const state = await demoState(page);
  const txn = state.data.transactions.at(-1);
  expect(txn.amountPiastres).toBe(76_000);
  expect(txn.receiptLocale).toBe('ar');
  expect(state.data.ledger.filter((entry: { transactionId?: string }) => entry.transactionId === txn.id)).toHaveLength(1);
  expect(state.data.wallets['co-nile'].balancePiastres).toBe(84_235_000 - 76_000);

  // Compact receipt.
  await page.getByRole('button', { name: 'طباعة الإيصال' }).click();
  await expect(page.getByText('تمت طباعة الإيصال')).toBeVisible();
  await expect(page.getByRole('figure', { name: 'إيصال حراري 58 مم' })).toBeVisible();
});

test('cooldown: scanning the same tag again is rejected before anything else', async ({ page }) => {
  await openStyled(page, '/pos');
  await posSignIn(page);
  await posFuel(page, '40');
  await expect(page.getByText('تمت العملية بنجاح')).toBeVisible();
  await page.getByRole('button', { name: 'إنهاء' }).click();

  await page.getByRole('button', { name: 'مسح المركبة' }).click();
  await expect(page.getByText('التزود بالوقود غير متاح حاليًا')).toBeVisible();
  await expect(page.getByText(/^(2:59|3:00) ساعة$/)).toBeVisible();
});

test('daily limit: س م ر 1274 is declined at confirmation without a debit', async ({ page }) => {
  await openStyled(page, '/pos');
  await posSignIn(page);
  const panel = await presenter(page);
  await panel.getByRole('combobox').selectOption('tag-veh-smr-1274');
  await page.keyboard.press('Control+Shift+P');
  const before = (await demoState(page)).data.wallets['co-nile'].balancePiastres;

  await posFuel(page, '40');
  await expect(page.getByText('تعذر تنفيذ العملية')).toBeVisible();
  await expect(page.getByText('تم الوصول إلى الحد اليومي للمركبة.')).toBeVisible();
  expect((await demoState(page)).data.wallets['co-nile'].balancePiastres).toBe(before);
});

test('printer failure: the transaction still succeeds, debits once, and the digital receipt is offered', async ({ page }) => {
  await openStyled(page, '/pos');
  await posSignIn(page);
  const panel = await presenter(page);
  await panel.getByRole('radio', { name: 'معطلة' }).click();
  await page.keyboard.press('Control+Shift+P');

  await posFuel(page, '40');
  await expect(page.getByText('تمت العملية بنجاح')).toBeVisible();
  await page.getByRole('button', { name: 'طباعة الإيصال' }).click();
  await expect(page.getByText('تعذرت الطباعة')).toBeVisible();
  await page.getByRole('button', { name: 'إعادة المحاولة' }).click();
  await expect(page.getByText('تعذرت الطباعة')).toBeVisible();

  const state = await demoState(page);
  const completed = state.data.transactions.filter(
    (txn: { vehicleId: string; status: string; receivedAt: string }) =>
      txn.vehicleId === 'veh-abg-4582' && txn.status === 'completed' && Date.now() - new Date(txn.receivedAt).getTime() < 60_000,
  );
  expect(completed).toHaveLength(1);
  expect(state.data.wallets['co-nile'].balancePiastres).toBe(84_235_000 - 76_000);

  await page.getByRole('button', { name: 'عرض الإيصال' }).click();
  await expect(page.getByRole('figure', { name: 'إيصال حراري 58 مم' })).toBeVisible();
});

test('offline: capture is queued, not shown to the company, then syncs when the network returns', async ({ context, page }) => {
  const company = await context.newPage();
  await company.setViewportSize({ width: 1440, height: 900 });
  await openStyled(company, '/company/transactions');

  await openStyled(page, '/pos');
  await posSignIn(page);
  const panel = await presenter(page);
  await panel.getByRole('radio', { name: 'غير متصل', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'وضع عدم الاتصال' })).toBeVisible();
  await page.keyboard.press('Control+Shift+P');

  await posFuel(page, '40');
  await expect(page.getByText('تمت العملية بدون اتصال')).toBeVisible();
  await expect(page.getByText('بانتظار المزامنة').first()).toBeVisible();

  let state = await demoState(page);
  expect(state.posQueue).toHaveLength(1);
  expect(state.posQueue[0].request.origin).toBe('offline');
  expect(state.data.wallets['co-nile'].balancePiastres).toBe(84_235_000);
  const offlineReference: string = state.posQueue[0].request.shortId;
  // Not server-confirmed yet: the company does not see it while the device is offline.
  await expect(company.getByText(offlineReference)).toHaveCount(0);

  const panelAgain = await presenter(page);
  await panelAgain.getByRole('radio', { name: 'متصل', exact: true }).click();
  await expect(page.getByText('جارٍ المزامنة…')).toBeVisible();
  await expect(page.getByText('تمت المزامنة').first()).toBeVisible({ timeout: 10_000 });

  state = await demoState(page);
  expect(state.posQueue).toHaveLength(0);
  const synced = state.data.transactions.at(-1);
  expect(synced.origin).toBe('offline');
  expect(synced.syncedAt).toBeTruthy();
  expect(state.data.wallets['co-nile'].balancePiastres).toBe(84_235_000 - 76_000);
  expect(synced.shortId).toBe(offlineReference);
  await expect(company.getByText(offlineReference).first()).toBeVisible();
});
