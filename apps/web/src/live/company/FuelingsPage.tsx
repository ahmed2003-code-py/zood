import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { ViewOnlyNote } from '@/shell/page';
import { companyApi } from '../api';
import { FuelingsList } from '../fuelings';

/** Company portal: the company's own fuelings, read only. */
export function CompanyFuelingsPage() {
  const { t } = useTranslation('live');
  const navigate = useNavigate();
  return (
    <>
      <FuelingsList
        portal="company"
        title={t('fuelings.title')}
        description={t('fuelings.companyDescription')}
        load={(query, signal) => companyApi.fuelings(query, signal)}
        onOpen={(fueling) => navigate(`/company/transactions/${fueling.id}`)}
      />
      <div className="mt-4">
        <ViewOnlyNote />
      </div>
    </>
  );
}
