/**
 * POS fueling flow state machine:
 * home → scanning → verify → fuel → confirm → processing → success (→ receipt) | declined.
 * Online confirmations go through the API (server engine); offline ones are validated against the
 * cached snapshot and queued on the device (PRD §7.4).
 */
import { useCallback, useRef, useState } from 'react';
import { api } from '@/api';
import type { FuelRequest } from '@/domain/fueling';
import { amountForVolume, type Milliliters, type Piastres } from '@/domain/money';
import { evaluateScan, type ScanResult } from '@/domain/pos';
import type { DeclineReason, IsoDateTime } from '@/domain/types';
import { receiptFromQueued, receiptFromTransaction, type ReceiptModel } from '@/features/receipt/model';
import { i18n } from '@/i18n';
import { isLocale } from '@/lib/locale';
import { DEMO_CONTEXT } from '@/mocks/personas';
import { useDemoStore } from '@/mocks/store';

export type OkScan = Extract<ScanResult, { ok: true }>;

export type PosStep =
  | { name: 'home' }
  | { name: 'scanning' }
  | { name: 'verify'; scan: OkScan }
  | { name: 'fuel'; scan: OkScan }
  | { name: 'confirm'; scan: OkScan; volumeMl: Milliliters; amountPiastres: Piastres; clientTxnId: string }
  | { name: 'processing'; offline: boolean }
  | { name: 'success'; receipt: ReceiptModel; offline: boolean }
  | { name: 'declined'; reason: DeclineReason | 'unknownTag'; plate?: string; cooldownUntil?: IsoDateTime }
  | { name: 'receipt'; receipt: ReceiptModel; back: 'success' | 'history'; offline?: boolean }
  | { name: 'history' };

const SCAN_DURATION_MS = 1100;
const MIN_PROCESSING_MS = 700;

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** Device-assigned receipt reference for offline captures (production: device id + monotonic sequence). */
function offlineReference(deviceCode: string, clientTxnId: string): string {
  return `TX-${deviceCode.replace(/^POS-/, '').replace('-', '')}-${clientTxnId.slice(0, 4).toUpperCase()}`;
}

export function usePosFlow(agentId: string) {
  const [step, setStep] = useState<PosStep>({ name: 'home' });
  const scanTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const goHome = useCallback(() => {
    clearTimeout(scanTimer.current);
    setStep({ name: 'home' });
  }, []);

  const startScan = useCallback(() => {
    setStep({ name: 'scanning' });
    clearTimeout(scanTimer.current);
    scanTimer.current = setTimeout(() => {
      const state = useDemoStore.getState();
      const tagId = state.simulation.selectedTagId;
      const result = evaluateScan(state.data, {
        tagId,
        stationId: DEMO_CONTEXT.stationId,
        now: new Date(),
        tagCooldownUntil: state.tagMemory[tagId],
      });
      if (result.ok) setStep({ name: 'verify', scan: result });
      else setStep({ name: 'declined', reason: result.reason, plate: result.vehicle?.plate, cooldownUntil: result.cooldownUntil });
    }, SCAN_DURATION_MS);
  }, []);

  const toFuel = useCallback((scan: OkScan) => setStep({ name: 'fuel', scan }), []);


  const confirm = useCallback(
    async (current: Extract<PosStep, { name: 'confirm' }>) => {
      const state = useDemoStore.getState();
      const offline = state.simulation.network === 'offline';
      const locale = isLocale(i18n.language) ? i18n.language : 'ar';
      const device = state.data.devices[DEMO_CONTEXT.posDeviceId];
      const request: FuelRequest = {
        clientTxnId: current.clientTxnId,
        tagId: current.scan.tag.id,
        stationId: DEMO_CONTEXT.stationId,
        deviceId: DEMO_CONTEXT.posDeviceId,
        agentId,
        volumeMl: current.volumeMl,
        origin: offline ? 'offline' : 'online',
        deviceAt: new Date().toISOString(),
        receiptLocale: locale,
        shortId: offline ? offlineReference(device?.code ?? 'POS', current.clientTxnId) : undefined,
      };
      setStep({ name: 'processing', offline });
      const started = Date.now();

      if (offline) {
        const result = useDemoStore.getState().captureOffline(request);
        await wait(Math.max(0, MIN_PROCESSING_MS - (Date.now() - started)));
        if (result.ok) setStep({ name: 'success', offline: true, receipt: receiptFromQueued(useDemoStore.getState().data, result.item) });
        else setStep({ name: 'declined', reason: result.reason, plate: current.scan.vehicle.plate });
        return;
      }

      const outcome = await api.fueling.record(request);
      await wait(Math.max(0, MIN_PROCESSING_MS - (Date.now() - started)));
      const txn = outcome.transaction;
      if (txn.status === 'completed') {
        setStep({ name: 'success', offline: false, receipt: receiptFromTransaction(useDemoStore.getState().data, txn) });
      } else {
        setStep({ name: 'declined', reason: txn.declineReason ?? 'tagInactive', plate: current.scan.vehicle.plate });
      }
    },
    [agentId],
  );

  const review = useCallback(
    (scan: OkScan, volumeMl: Milliliters) => {
      // The idempotency key is created once per confirmation, so a double tap or retry cannot charge twice.
      const next: Extract<PosStep, { name: 'confirm' }> = {
        name: 'confirm',
        scan,
        volumeMl,
        amountPiastres: amountForVolume(volumeMl, scan.unitPricePiastres),
        clientTxnId: crypto.randomUUID(),
      };
      // VIP tier: frictionless flow, no confirmation sheet (PRD §4.4).
      if (scan.vehicle.tier === 'vip') void confirm(next);
      else setStep(next);
    },
    [confirm],
  );

  return { step, setStep, goHome, startScan, toFuel, review, confirm };
}
