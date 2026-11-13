import { Navigate, Route, Routes } from 'react-router';
import { RequirePortal } from '@/auth/RequirePortal';
import { API_MODE } from '@/api';
import { NotYetLive } from '@/live/NotYetLive';
import { StationFuelingsPage } from '@/live/station/FuelingsPage';
import { StationNetworkPage } from '@/live/station/NetworkPage';
import { PortalShell } from '@/shell/PortalShell';
import { MorePage, SettingsPage } from '@/shell/StandardPages';
import { stationPortal } from './config';
import { StationDevices, StationHome, StationSettlements, StationShifts, StationTransactions } from './pages';

/** Against the API, the network comes from the server; the in-browser demo keeps its own screens. */
const live = API_MODE === 'http';

/** Station manager portal (lazy-loaded chunk). */
export function StationApp() {
  return (
    <RequirePortal portal="station">
      <PortalShell config={stationPortal}>
        <Routes>
          <Route index element={live ? <Navigate to="/station/devices" replace /> : <StationHome />} />
          <Route path="transactions" element={live ? <StationFuelingsPage /> : <StationTransactions />} />
          <Route path="devices" element={live ? <StationNetworkPage /> : <StationDevices />} />
          <Route path="shifts" element={live ? <NotYetLive phase="B5" portal="station" /> : <StationShifts />} />
          <Route path="settlements" element={live ? <NotYetLive phase="B6" portal="station" /> : <StationSettlements />} />
          <Route path="settings" element={<SettingsPage config={stationPortal} />} />
          <Route path="more" element={<MorePage config={stationPortal} />} />
          <Route path="*" element={<Navigate to="/station" replace />} />
        </Routes>
      </PortalShell>
    </RequirePortal>
  );
}
