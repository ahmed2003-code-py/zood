import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router';
import { PageHeader } from '@/shell/page';
import { opsApi } from '../api';
import { ActivityCalendar } from '../calendar';

/** Ops: what happened each day on the platform, or for one company, station, vehicle, device or agent (query parameters). */
export function ActivityPage() {
  const { t } = useTranslation('live');
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const filter = { companyId: params.get('companyId') ?? undefined, stationId: params.get('stationId') ?? undefined, vehicleId: params.get('vehicleId') ?? undefined, deviceId: params.get('deviceId') ?? undefined, agentId: params.get('agentId') ?? undefined };
  return (
    <>
      <PageHeader title={t('calendar.title')} description={t('calendar.opsDescription')} />
      <ActivityCalendar
        load={(from, to, signal) => opsApi.activity({ from, to, ...filter }, signal)}
        onOpenDay={(day) => navigate(`/ops/transactions?from=${day}&to=${day}`)}
      />
    </>
  );
}
