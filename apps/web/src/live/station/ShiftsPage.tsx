import { useTranslation } from 'react-i18next';
import { stationApi } from '../api';
import { ShiftsList } from '../shifts';

/** Station portal: the shifts of the operator's pump agents, newest first. */
export function StationShiftsPage() {
  const { t } = useTranslation('live');
  return (
    <ShiftsList
      base="/station"
      title={t('shifts.title')}
      description={t('shifts.stationDescription')}
      load={(page, signal) => stationApi.shifts(page, signal)}
      loadStations={(signal) => stationApi.stations({ limit: 100 }, signal)}
    />
  );
}
