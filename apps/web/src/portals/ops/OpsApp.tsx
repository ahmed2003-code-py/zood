import { Navigate, Route, Routes } from 'react-router';
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

/** ZAWED Operations console (lazy-loaded chunk). */
export function OpsApp() {
  return (
    <PortalShell config={opsPortal}>
      <Routes>
        <Route index element={<OpsHome />} />
        <Route path="companies" element={<OpsCompanies />} />
        <Route path="topups" element={<OpsTopups />} />
        <Route path="stations" element={<OpsStations />} />
        <Route path="devices" element={<OpsDevices />} />
        <Route path="sync" element={<OpsSync />} />
        <Route path="transactions" element={<OpsTransactions />} />
        <Route path="transactions/:id" element={<OpsTransactionDetail />} />
        <Route path="alerts" element={<OpsAlerts />} />
        <Route path="alerts/:id" element={<OpsAlertDetail />} />
        <Route path="settlements" element={<OpsSettlements />} />
        <Route path="audit" element={<OpsAudit />} />
        <Route path="settings" element={<SettingsPage config={opsPortal} />} />
        <Route path="more" element={<MorePage config={opsPortal} />} />
        <Route path="*" element={<Navigate to="/ops" replace />} />
      </Routes>
    </PortalShell>
  );
}
