import type { FuelTransaction } from '@/domain/types';
import type { PeriodFilter } from '@/mocks/selectors';

export interface FilterValues {
  period: PeriodFilter;
  status?: FuelTransaction['status'];
  vehicleId?: string;
  stationId?: string;
}

export const DEFAULT_FILTERS: FilterValues = { period: 'month' };
