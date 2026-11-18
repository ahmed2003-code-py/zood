import { useTranslation } from 'react-i18next';
import { opsApi } from '../api';
import { ShiftsList } from '../shifts';

/** Ops: the shifts of every pump agent on the platform, newest first. */
export function ShiftsPage() {
  const { t } = useTranslation('live');
  return (
    <ShiftsList
      base="/ops"
      title={t('shifts.title')}
      description={t('shifts.opsDescription')}
      load={(page, signal) => opsApi.shifts(page, signal)}
      loadStations={(signal) => opsApi.stations.list({ limit: 100 }, signal)}
    />
  );
}
