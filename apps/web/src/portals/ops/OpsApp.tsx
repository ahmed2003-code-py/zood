import { Navigate, Route, Routes } from 'react-router';
import { API_MODE } from '@/api';
import { RequirePortal } from '@/auth/RequirePortal';
import { CompaniesPage } from '@/live/ops/CompaniesPage';
import { CompanyPage } from '@/live/ops/CompanyPage';
import { NotYetLive } from '@/live/NotYetLive';
import { DevicesPage } from '@/live/ops/DevicesPage';
import { OperatorPage } from '@/live/ops/OperatorPage';
import { PricesPage } from '@/live/ops/PricesPage';
import { StationPage } from '@/live/ops/StationPage';
import { StationsPage } from '@/live/ops/StationsPage';
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
          <Route path="topups" element={live ? <NotYetLive phase="B3" portal="ops" /> : <OpsTopups />} />
          <Route path="stations" element={live ? <StationsPage /> : <OpsStations />} />
          {live && <Route path="stations/:id" element={<StationPage />} />}
          {live && <Route path="operators/:id" element={<OperatorPage />} />}
          <Route path="devices" element={live ? <DevicesPage /> : <OpsDevices />} />
          {live && <Route path="prices" element={<PricesPage />} />}
          <Route path="sync" element={live ? <NotYetLive phase="B5" portal="ops" /> : <OpsSync />} />
          <Route path="transactions" element={live ? <NotYetLive phase="B4" portal="ops" /> : <OpsTransactions />} />
          <Route path="transactions/:id" element={live ? <NotYetLive phase="B4" portal="ops" /> : <OpsTransactionDetail />} />
          <Route path="alerts" element={live ? <NotYetLive phase="B4" portal="ops" /> : <OpsAlerts />} />
          <Route path="alerts/:id" element={live ? <NotYetLive phase="B4" portal="ops" /> : <OpsAlertDetail />} />
          <Route path="settlements" element={live ? <NotYetLive phase="B6" portal="ops" /> : <OpsSettlements />} />
          <Route path="audit" element={live ? <NotYetLive phase="B6" portal="ops" /> : <OpsAudit />} />
          <Route path="settings" element={<SettingsPage config={opsPortal} />} />
          <Route path="more" element={<MorePage config={opsPortal} />} />
          <Route path="*" element={<Navigate to="/ops" replace />} />
        </Routes>
      </PortalShell>
    </RequirePortal>
  );
}
