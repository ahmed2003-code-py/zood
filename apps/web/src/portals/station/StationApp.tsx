import { Navigate, Route, Routes } from 'react-router';
import { ActivityPage as LiveActivityPage } from '@/live/station/ActivityPage';
import { RequirePortal } from '@/auth/RequirePortal';
import { API_MODE } from '@/api';
import { StationFuelingsPage } from '@/live/station/FuelingsPage';
import { StationNetworkPage } from '@/live/station/NetworkPage';
import { StationSettlementsPage } from '@/live/station/SettlementsPage';
import { StationShiftsPage } from '@/live/station/ShiftsPage';
import { TicketsPage, TicketThreadPage } from '@/live/tickets';
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
          {live && <Route path="activity" element={<LiveActivityPage />} />}
          <Route path="transactions" element={live ? <StationFuelingsPage /> : <StationTransactions />} />
          <Route path="devices" element={live ? <StationNetworkPage /> : <StationDevices />} />
          <Route path="shifts" element={live ? <StationShiftsPage /> : <StationShifts />} />
          <Route path="settlements" element={live ? <StationSettlementsPage /> : <StationSettlements />} />
          {live && <Route path="tickets" element={<TicketsPage portal="station" />} />}
          {live && <Route path="tickets/:id" element={<TicketThreadPage portal="station" />} />}
          <Route path="settings" element={<SettingsPage config={stationPortal} />} />
          <Route path="more" element={<MorePage config={stationPortal} />} />
          <Route path="*" element={<Navigate to="/station" replace />} />
        </Routes>
      </PortalShell>
    </RequirePortal>
  );
}
