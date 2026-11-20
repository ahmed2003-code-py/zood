/**
 * Landing page pricing.
 *
 * PLACEHOLDER: every amount in this file is a placeholder chosen for layout only. The owner sets the
 * real prices before launch (docs/design/landing-spec.md, "Placeholders"). The page labels them as
 * indicative and asks visitors to contact us for a quote. Change the numbers here and nowhere else.
 *
 * Money is integer piastres (100 piastres = 1 EGP), like everywhere else in the app.
 */
import type { FleetSize } from '@/domain/demo-request';
import type { Piastres } from '@/domain/money';

/** Shown on every price in the UI while this is true. */
export const PRICES_ARE_PLACEHOLDERS = true;

export type BillingPeriod = 'monthly' | 'yearly';

export interface PricingTier {
  id: 'start' | 'growth' | 'business' | 'fleet' | 'enterprise';
  /** The same buckets as the demo form's fleet-size select. */
  fleetSize: FleetSize;
  /** PLACEHOLDER. Per vehicle per month; undefined means "custom quote". */
  monthlyPerVehicle?: Piastres;
}

export const PRICING_TIERS: readonly PricingTier[] = [
  { id: 'start', fleetSize: '1-19', monthlyPerVehicle: 15_000 }, // PLACEHOLDER: EGP 150
  { id: 'growth', fleetSize: '20-49', monthlyPerVehicle: 13_000 }, // PLACEHOLDER: EGP 130
  { id: 'business', fleetSize: '50-99', monthlyPerVehicle: 11_500 }, // PLACEHOLDER: EGP 115
  { id: 'fleet', fleetSize: '100-249', monthlyPerVehicle: 10_000 }, // PLACEHOLDER: EGP 100
  { id: 'enterprise', fleetSize: '250+' }, // custom quote
];

/** PLACEHOLDER. Yearly billing charges this many months for twelve. */
export const YEARLY_BILLED_MONTHS = 10;

/** PLACEHOLDER. One-time price of the RFID tag fitted to each vehicle. */
export const TAG_PRICE_PER_VEHICLE: Piastres = 25_000; // PLACEHOLDER: EGP 250

/** The price shown for a tier: per vehicle per month, or per vehicle per year when billed yearly. */
export function tierPrice(tier: PricingTier, period: BillingPeriod): Piastres | undefined {
  if (tier.monthlyPerVehicle === undefined) return undefined;
  return period === 'monthly' ? tier.monthlyPerVehicle : tier.monthlyPerVehicle * YEARLY_BILLED_MONTHS;
}

/** The pricing buckets must stay the form's buckets, in the same order (checked by pricing.test.ts). */
export const PRICING_FLEET_SIZES: readonly FleetSize[] = PRICING_TIERS.map((tier) => tier.fleetSize);
