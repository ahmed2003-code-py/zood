import { Navigate, Route, Routes } from 'react-router';
import { RequirePortal } from '@/auth/RequirePortal';
import { PortalShell } from '@/shell/PortalShell';
import { MorePage, SettingsPage } from '@/shell/StandardPages';
import { stationPortal } from './config';
import { StationDevices, StationHome, StationSettlements, StationShifts, StationTransactions } from './pages';

/** Station manager portal (lazy-loaded chunk). */
export function StationApp() {
  return (
    <RequirePortal portal="station">
      <PortalShell config={stationPortal}>
        <Routes>
          <Route index element={<StationHome />} />
          <Route path="transactions" element={<StationTransactions />} />
          <Route path="devices" element={<StationDevices />} />
          <Route path="shifts" element={<StationShifts />} />
          <Route path="settlements" element={<StationSettlements />} />
          <Route path="settings" element={<SettingsPage config={stationPortal} />} />
          <Route path="more" element={<MorePage config={stationPortal} />} />
          <Route path="*" element={<Navigate to="/station" replace />} />
        </Routes>
      </PortalShell>
    </RequirePortal>
  );
}
