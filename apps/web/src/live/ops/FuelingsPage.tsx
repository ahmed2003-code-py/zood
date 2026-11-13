import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { opsApi } from '../api';
import { FuelingsList } from '../fuelings';

/** Ops: every fueling on the platform, newest first. */
export function FuelingsPage() {
  const { t } = useTranslation('live');
  const navigate = useNavigate();
  return (
    <FuelingsList
      portal="ops"
      title={t('fuelings.title')}
      description={t('fuelings.opsDescription')}
      load={(query, signal) => opsApi.fuelings.list(query, signal)}
      onOpen={(fueling) => navigate(`/ops/transactions/${fueling.id}`)}
    />
  );
}
