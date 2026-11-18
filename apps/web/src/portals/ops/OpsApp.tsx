import { Navigate, Route, Routes } from 'react-router';
import { ActivityPage as LiveActivityPage } from '@/live/ops/ActivityPage';
import { API_MODE } from '@/api';
import { RequirePortal } from '@/auth/RequirePortal';
import { AlertsPage } from '@/live/ops/AlertsPage';
import { AuditPage } from '@/live/ops/AuditPage';
import { SyncPage as LiveSyncPage } from '@/live/ops/SyncPage';
import { CompaniesPage } from '@/live/ops/CompaniesPage';
import { CompanyPage } from '@/live/ops/CompanyPage';
import { DevicesPage } from '@/live/ops/DevicesPage';
import { FuelingPage } from '@/live/ops/FuelingPage';
import { FuelingsPage } from '@/live/ops/FuelingsPage';
import { OperatorPage } from '@/live/ops/OperatorPage';
import { PricesPage } from '@/live/ops/PricesPage';
import { StationPage } from '@/live/ops/StationPage';
import { StationsPage } from '@/live/ops/StationsPage';
import { ReconciliationPage } from '@/live/ops/ReconciliationPage';
import { SettlementsPage } from '@/live/ops/SettlementsPage';
import { ShiftsPage } from '@/live/ops/ShiftsPage';
import { TicketsPage, TicketThreadPage } from '@/live/tickets';
import { TopUpsPage } from '@/live/ops/TopUpsPage';
import { UsersPage } from '@/live/ops/UsersPage';
import { VehiclePage } from '@/live/ops/VehiclePage';
import { PortalShell } from '@/shell/PortalShell';
import { MorePage, SettingsPage } from '@/shell/StandardPages';
import { opsPortal } from './config';
import { OpsAlertDetail, OpsTransactionDetail } from './investigation';
import {
  OpsAlerts,
  OpsAudit,
  OpsCompanies,
  OpsDevices,
  OpsHome,
  OpsSettlements,
  OpsStations,
  OpsSync,
  OpsTopups,
  OpsTransactions,
} from './pages';

/** Against the API, master data comes from the server; the in-browser demo keeps its own screens. */
const live = API_MODE === 'http';

/** ZAWED Operations console (lazy-loaded chunk). */
export function OpsApp() {
  return (
    <RequirePortal portal="ops">
      <PortalShell config={opsPortal}>
        <Routes>
          <Route index element={live ? <Navigate to="/ops/companies" replace /> : <OpsHome />} />
          <Route path="companies" element={live ? <CompaniesPage /> : <OpsCompanies />} />
          {live && <Route path="companies/:id" element={<CompanyPage />} />}
          {live && <Route path="vehicles/:id" element={<VehiclePage />} />}
          <Route path="topups" element={live ? <TopUpsPage /> : <OpsTopups />} />
          <Route path="stations" element={live ? <StationsPage /> : <OpsStations />} />
          {live && <Route path="stations/:id" element={<StationPage />} />}
          {live && <Route path="operators/:id" element={<OperatorPage />} />}
          <Route path="devices" element={live ? <DevicesPage /> : <OpsDevices />} />
          {live && <Route path="prices" element={<PricesPage />} />}
          <Route path="sync" element={live ? <LiveSyncPage /> : <OpsSync />} />
          {live && <Route path="activity" element={<LiveActivityPage />} />}
          <Route path="transactions" element={live ? <FuelingsPage /> : <OpsTransactions />} />
          <Route path="transactions/:id" element={live ? <FuelingPage /> : <OpsTransactionDetail />} />
          <Route path="alerts" element={live ? <AlertsPage /> : <OpsAlerts />} />
          <Route path="alerts/:id" element={live ? <AlertsPage /> : <OpsAlertDetail />} />
          <Route path="settlements" element={live ? <SettlementsPage /> : <OpsSettlements />} />
          {live && <Route path="shifts" element={<ShiftsPage />} />}
          {live && <Route path="tickets" element={<TicketsPage portal="ops" />} />}
          {live && <Route path="tickets/:id" element={<TicketThreadPage portal="ops" />} />}
          {live && <Route path="reconciliation" element={<ReconciliationPage />} />}
          {live && <Route path="users" element={<UsersPage />} />}
          <Route path="audit" element={live ? <AuditPage /> : <OpsAudit />} />
          <Route path="settings" element={<SettingsPage config={opsPortal} />} />
          <Route path="more" element={<MorePage config={opsPortal} />} />
          <Route path="*" element={<Navigate to="/ops" replace />} />
        </Routes>
      </PortalShell>
    </RequirePortal>
  );
}
