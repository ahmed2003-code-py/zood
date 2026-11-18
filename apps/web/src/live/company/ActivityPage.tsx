import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router';
import { PageHeader } from '@/shell/page';
import { companyApi } from '../api';
import { ActivityCalendar } from '../calendar';

/** The company's calendar, or one vehicle's (?vehicleId=). */
export function ActivityPage() {
  const { t } = useTranslation('live');
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const filter = { vehicleId: params.get('vehicleId') ?? undefined };
  return (
    <>
      <PageHeader title={t('calendar.title')} description={t('calendar.companyDescription')} />
      <ActivityCalendar
        load={(from, to, signal) => companyApi.activity({ from, to, ...filter }, signal)}
        onOpenDay={(day) => navigate(`/company/transactions?from=${day}&to=${day}`)}
      />
    </>
  );
}
