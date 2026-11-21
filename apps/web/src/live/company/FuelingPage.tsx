import { LifeBuoy, Truck } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, Icon } from '@/design-system';
import { companyApi } from '../api';
import { FuelingDetailPage } from '../fuelings';

/** Company portal: one of the company's fuelings, read only, with a way to raise a problem about it. */
export function CompanyFuelingPage() {
  const { t } = useTranslation('portal');
  return (
    <FuelingDetailPage
      portal="company"
      backTo="/company/transactions"
      load={(id, signal) => companyApi.fueling(id, signal)}
      renderActions={(fueling) => (
        <>
          <Button asChild variant="secondary">
            <Link to={`/company/vehicles/${fueling.vehicleId}`}>
              <Icon icon={Truck} size={18} />
              {t('fueling.vehicle')}
            </Link>
          </Button>
          <Button asChild variant="secondary">
            <Link to={`/company/tickets?new=1&type=DISPUTE&fuelingId=${fueling.id}&vehicleId=${fueling.vehicleId}`}>
              <Icon icon={LifeBuoy} size={18} />
              {t('fueling.report')}
            </Link>
          </Button>
        </>
      )}
    />
  );
}
