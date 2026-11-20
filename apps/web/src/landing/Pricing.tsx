import { Check, Info, Nfc } from 'lucide-react';
import { useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Icon, SegmentedControl } from '@/design-system';
import type { FleetSize } from '@/domain/demo-request';
import { useLocale } from '@/i18n/useLocale';
import { formatMoney } from '@/lib/format';
import { PRICING_TIERS, TAG_PRICE_PER_VEHICLE, tierPrice, YEARLY_BILLED_MONTHS, type BillingPeriod, type PricingTier } from './pricing';

const INCLUDED = ['portal', 'limits', 'alerts', 'statements', 'support'] as const;

function range(tier: PricingTier): { from: string; to?: string } {
  const [from = '', to] = tier.fleetSize.replace('+', '').split('-');
  return { from, to };
}

/**
 * Pricing by fleet size. Every number comes from pricing.ts, which is a PLACEHOLDER until the
 * owner sets real prices; the UI says so on the section and on every price.
 */
export function Pricing({ onChoose }: { onChoose: (fleetSize: FleetSize) => void }) {
  const { t } = useTranslation('landing');
  const { locale } = useLocale();
  const [period, setPeriod] = useState<BillingPeriod>('monthly');

  return (
    <section id="pricing" aria-labelledby="pricing-title" className="scroll-mt-16 bg-surface py-20 md:py-28">
      <div className="mx-auto w-full max-w-6xl px-4 md:px-6">
        <div data-reveal className="flex flex-col gap-6 lg:flex-row lg:items-end lg:justify-between">
          <div className="max-w-2xl">
            <h2 id="pricing-title" className="landing-display text-3xl leading-tight font-bold text-balance md:text-[2.5rem]">
              {t('pricing.title')}
            </h2>
            <p className="mt-4 text-lg leading-relaxed text-pretty text-muted">{t('pricing.lead')}</p>
          </div>
          <div className="flex flex-col items-start gap-2 lg:items-end">
            <SegmentedControl<BillingPeriod>
              label={t('pricing.period')}
              value={period}
              onValueChange={setPeriod}
              options={[
                { value: 'monthly', label: t('pricing.monthly') },
                { value: 'yearly', label: t('pricing.yearly') },
              ]}
            />
            <span className="text-sm text-muted">{t('pricing.yearlyHint', { months: YEARLY_BILLED_MONTHS })}</span>
          </div>
        </div>

        <p data-reveal className="mt-8 flex items-start gap-2 rounded-control border border-warning/30 bg-warning-subtle px-4 py-3 text-sm font-medium text-warning">
          <Icon icon={Info} size={18} className="mt-px" />
          {t('pricing.notice')}
        </p>

        <ul className="mt-8 grid grid-cols-2 gap-3 sm:gap-4 md:grid-cols-3 lg:grid-cols-5">
          {PRICING_TIERS.map((tier, index) => {
            const price = tierPrice(tier, period);
            const { from, to } = range(tier);
            return (
              <li
                key={tier.id}
                data-reveal
                style={{ '--reveal-delay': `${index * 60}ms` } as CSSProperties}
                className="flex flex-col rounded-container border border-border bg-background p-4 last:col-span-2 sm:p-5 md:last:col-span-1"
              >
                <h3 className="text-lg font-semibold">{t(`pricing.tiers.${tier.id}.name`)}</h3>
                <p className="mt-1 text-sm text-muted">{to ? t('pricing.vehicles', { from, to }) : t('pricing.vehiclesOpen', { from })}</p>
                <div className="mt-5 min-h-[5.5rem]">
                  {price !== undefined ? (
                    <>
                      <p className="text-[1.75rem] leading-tight font-bold tabular-nums">{formatMoney(price, locale, { precision: 'whole' })}</p>
                      <p className="mt-1 text-sm text-muted">{t(period === 'monthly' ? 'pricing.perMonth' : 'pricing.perYear')}</p>
                    </>
                  ) : (
                    <>
                      <p className="text-[1.75rem] leading-tight font-bold">{t('pricing.custom')}</p>
                      <p className="mt-1 text-sm text-muted">{t('pricing.customHint')}</p>
                    </>
                  )}
                </div>
                <span className="mt-3 self-start rounded-full bg-warning-subtle px-2 py-0.5 text-xs font-medium text-warning">{t('pricing.indicative')}</span>
                <button
                  type="button"
                  onClick={() => onChoose(tier.fleetSize)}
                  className="mt-6 inline-flex min-h-11 items-center justify-center rounded-control border border-border-strong px-4 text-sm font-semibold transition-colors hover:border-primary hover:bg-primary-subtle hover:text-primary-subtle-foreground"
                >
                  {t('pricing.cta')}
                </button>
              </li>
            );
          })}
        </ul>

        <div data-reveal className="mt-10 grid gap-8 border-t border-border pt-8 md:grid-cols-[1.4fr_1fr]">
          <div>
            <h3 className="text-base font-semibold">{t('pricing.includedTitle')}</h3>
            <ul className="mt-4 grid gap-x-8 gap-y-2.5 text-sm sm:grid-cols-2">
              {INCLUDED.map((id) => (
                <li key={id} className="flex items-center gap-2">
                  <Icon icon={Check} size={16} className="text-success" />
                  {t(`pricing.included.${id}`)}
                </li>
              ))}
            </ul>
          </div>
          <div className="flex flex-col gap-3 text-sm text-muted">
            <p className="flex items-center gap-2 font-medium text-foreground">
              <Icon icon={Nfc} size={16} className="text-primary" />
              {t('pricing.tag', { price: formatMoney(TAG_PRICE_PER_VEHICLE, locale, { precision: 'whole' }) })}
            </p>
            <p>{t('pricing.stationsNote')}</p>
          </div>
        </div>
      </div>
    </section>
  );
}
