import { Navigate, Route, Routes } from 'react-router';
import { PortalShell } from '@/shell/PortalShell';
import { MorePage, SettingsPage } from '@/shell/StandardPages';
import { companyPortal } from './config';
import { CompanyTransactionDetail, CompanyTransactions, CompanyVehicleDetail, CompanyVehicles } from './fleet-pages';
import { CompanyAlerts, CompanyBudgets, CompanyHome, CompanyLimits, CompanyReports, CompanyRequests, CompanyWallet } from './pages';

/** Company portal (lazy-loaded chunk). */
export function CompanyApp() {
  return (
    <PortalShell config={companyPortal}>
      <Routes>
        <Route index element={<CompanyHome />} />
        <Route path="transactions" element={<CompanyTransactions />} />
        <Route path="transactions/:id" element={<CompanyTransactionDetail />} />
        <Route path="vehicles" element={<CompanyVehicles />} />
        <Route path="vehicles/:id" element={<CompanyVehicleDetail />} />
        <Route path="wallet" element={<CompanyWallet />} />
        <Route path="budgets" element={<CompanyBudgets />} />
        <Route path="limits" element={<CompanyLimits />} />
        <Route path="alerts" element={<CompanyAlerts />} />
        <Route path="reports" element={<CompanyReports />} />
        <Route path="requests" element={<CompanyRequests />} />
        <Route path="settings" element={<SettingsPage config={companyPortal} />} />
        <Route path="more" element={<MorePage config={companyPortal} />} />
        <Route path="*" element={<Navigate to="/company" replace />} />
      </Routes>
    </PortalShell>
  );
}
