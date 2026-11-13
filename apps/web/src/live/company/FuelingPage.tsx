import { companyApi } from '../api';
import { FuelingDetailPage } from '../fuelings';

/** Company portal: one of the company's fuelings, read only. */
export function CompanyFuelingPage() {
  return <FuelingDetailPage portal="company" backTo="/company/transactions" load={(id, signal) => companyApi.fueling(id, signal)} />;
}
