import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router';
import { PageHeader } from '@/shell/page';
import { stationApi } from '../api';
import { ActivityCalendar } from '../calendar';

/** The operator's calendar, or one station, device or agent's (query parameters). */
export function ActivityPage() {
  const { t } = useTranslation('live');
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const filter = { stationId: params.get('stationId') ?? undefined, deviceId: params.get('deviceId') ?? undefined, agentId: params.get('agentId') ?? undefined };
  return (
    <>
      <PageHeader title={t('calendar.title')} description={t('calendar.stationDescription')} />
      <ActivityCalendar
        load={(from, to, signal) => stationApi.activity({ from, to, ...filter }, signal)}
        onOpenDay={(day) => navigate(`/station/transactions?from=${day}&to=${day}`)}
      />
    </>
  );
}
