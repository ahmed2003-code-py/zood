import { useTranslation } from 'react-i18next';
import { StatusChip, type Tone } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { formatMoney, formatVolume } from '@/lib/format';
import type { Limit } from '../api';
import type { LimitUsage } from './model';
import { UsageMeter } from './ui';

const STATE_TONE: Record<LimitUsage['state'], Tone> = { ok: 'success', near: 'warning', reached: 'danger' };
const METER: Record<LimitUsage['state'], 'ok' | 'near' | 'over'> = { ok: 'ok', near: 'near', reached: 'over' };

/** "30 L of 50 L" or "EGP 900.00 of EGP 1,000.00": a limit's figures in its own unit. */
function useLimitFigures() {
  const { locale } = useLocale();
  return (limit: Limit, value: number) => (limit.kind === 'VOLUME' ? formatVolume(value, locale) : formatMoney(value, locale));
}

/** One limit's usage: what it is, a meter, and how much is left. */
export function LimitUsageRow({ usage }: { usage: LimitUsage }) {
  const { t } = useTranslation(['portal', 'live']);
  const figure = useLimitFigures();
  const { limit } = usage;
  const name = t('limits.rule', { period: t(`live:enums.limitPeriod.${limit.period}`), kind: t(`live:enums.limitKind.${limit.kind}`) });
  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
        <span className="text-sm font-medium">{name}</span>
        <StatusChip tone={STATE_TONE[usage.state]} label={t(`insights.limitState.${usage.state}`)} />
      </div>
      <UsageMeter percent={usage.percent} state={METER[usage.state]} label={t('insights.limitMeter', { name, percent: usage.percent })} />
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1 text-xs text-muted">
        <span className="tabular-nums">{t('insights.usedOf', { used: figure(limit, usage.used), max: figure(limit, limit.maxValue) })}</span>
        <span className="tabular-nums">{t('insights.left', { amount: figure(limit, usage.remaining) })}</span>
      </div>
    </div>
  );
}

