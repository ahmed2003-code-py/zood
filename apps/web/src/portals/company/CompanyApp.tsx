import { Navigate, Route, Routes } from 'react-router';
import { RequirePortal } from '@/auth/RequirePortal';
import { API_MODE } from '@/api';
import { CompanyVehiclePage } from '@/live/company/VehiclePage';
import { CompanyVehiclesPage } from '@/live/company/VehiclesPage';
import { CompanyWalletPage } from '@/live/company/WalletPage';
import { NotYetLive } from '@/live/NotYetLive';
import { PortalShell } from '@/shell/PortalShell';
import { MorePage, SettingsPage } from '@/shell/StandardPages';
import { companyPortal } from './config';
import { CompanyTransactionDetail, CompanyTransactions, CompanyVehicleDetail, CompanyVehicles } from './fleet-pages';
import {
  CompanyAlerts,
  CompanyBudgets,
  CompanyHome,
  CompanyLimits,
  CompanyReports,
  CompanyRequests,
  CompanyWallet,
} from './pages';

/** Against the API, the vehicles come from the server; the in-browser demo keeps its own screens. */
const live = API_MODE === 'http';

/** Company portal (lazy-loaded chunk). */
export function CompanyApp() {
  return (
    <RequirePortal portal="company">
      <PortalShell config={companyPortal}>
        <Routes>
          <Route index element={live ? <Navigate to="/company/vehicles" replace /> : <CompanyHome />} />
          <Route path="transactions" element={live ? <NotYetLive phase="B4" portal="company" /> : <CompanyTransactions />} />
          <Route
            path="transactions/:id"
            element={live ? <NotYetLive phase="B4" portal="company" /> : <CompanyTransactionDetail />}
          />
          <Route path="vehicles" element={live ? <CompanyVehiclesPage /> : <CompanyVehicles />} />
          <Route path="vehicles/:id" element={live ? <CompanyVehiclePage /> : <CompanyVehicleDetail />} />
          <Route path="wallet" element={live ? <CompanyWalletPage /> : <CompanyWallet />} />
          <Route path="budgets" element={live ? <NotYetLive phase="B4" portal="company" /> : <CompanyBudgets />} />
          <Route path="limits" element={live ? <NotYetLive phase="B4" portal="company" /> : <CompanyLimits />} />
          <Route path="alerts" element={live ? <NotYetLive phase="B4" portal="company" /> : <CompanyAlerts />} />
          <Route path="reports" element={live ? <NotYetLive phase="B4" portal="company" /> : <CompanyReports />} />
          <Route path="requests" element={live ? <NotYetLive phase="B6" portal="company" /> : <CompanyRequests />} />
          <Route path="settings" element={<SettingsPage config={companyPortal} />} />
          <Route path="more" element={<MorePage config={companyPortal} />} />
          <Route path="*" element={<Navigate to="/company" replace />} />
        </Routes>
      </PortalShell>
    </RequirePortal>
  );
}
