import { Navigate, Route, Routes } from 'react-router';
import { ActivityPage as LiveActivityPage } from '@/live/company/ActivityPage';
import { RequirePortal } from '@/auth/RequirePortal';
import { API_MODE } from '@/api';
import { CompanyAlertsPage } from '@/live/company/AlertsPage';
import { CompanyBudgetsPage } from '@/live/company/BudgetsPage';
import { CompanyHomePage } from '@/live/company/HomePage';
import { CompanyLimitsPage } from '@/live/company/LimitsPage';
import { CompanyReportsPage } from '@/live/company/ReportsPage';
import { CompanyFuelingPage } from '@/live/company/FuelingPage';
import { CompanyFuelingsPage } from '@/live/company/FuelingsPage';
import { CompanyVehiclePage } from '@/live/company/VehiclePage';
import { CompanyVehiclesPage } from '@/live/company/VehiclesPage';
import { CompanyWalletPage } from '@/live/company/WalletPage';
import { TicketsPage, TicketThreadPage } from '@/live/tickets';
import { PortalNotFound } from '@/shell/NotFoundPage';
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
          <Route index element={live ? <CompanyHomePage /> : <CompanyHome />} />
          {live && <Route path="activity" element={<LiveActivityPage />} />}
          <Route path="transactions" element={live ? <CompanyFuelingsPage /> : <CompanyTransactions />} />
          <Route
            path="transactions/:id"
            element={live ? <CompanyFuelingPage /> : <CompanyTransactionDetail />}
          />
          <Route path="vehicles" element={live ? <CompanyVehiclesPage /> : <CompanyVehicles />} />
          <Route path="vehicles/:id" element={live ? <CompanyVehiclePage /> : <CompanyVehicleDetail />} />
          <Route path="wallet" element={live ? <CompanyWalletPage /> : <CompanyWallet />} />
          <Route path="budgets" element={live ? <CompanyBudgetsPage /> : <CompanyBudgets />} />
          <Route path="limits" element={live ? <CompanyLimitsPage /> : <CompanyLimits />} />
          <Route path="alerts" element={live ? <CompanyAlertsPage /> : <CompanyAlerts />} />
          <Route path="reports" element={live ? <CompanyReportsPage /> : <CompanyReports />} />
          <Route path="requests" element={live ? <Navigate to="/company/tickets" replace /> : <CompanyRequests />} />
          {live && <Route path="tickets" element={<TicketsPage portal="company" />} />}
          {live && <Route path="tickets/:id" element={<TicketThreadPage portal="company" />} />}
          <Route path="settings" element={<SettingsPage config={companyPortal} />} />
          <Route path="more" element={<MorePage config={companyPortal} />} />
          <Route path="*" element={<PortalNotFound config={companyPortal} />} />
        </Routes>
      </PortalShell>
    </RequirePortal>
  );
}
