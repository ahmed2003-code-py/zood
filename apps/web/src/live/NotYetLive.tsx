import { Hourglass } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, EmptyState } from '@/design-system';
import { ListPanel } from '@/shell/page';

/** Each portal's main screen that already runs on live data. */
const LIVE_HOME = {
  ops: { to: '/ops/companies', label: 'pending.toCompanies' },
  company: { to: '/company/vehicles', label: 'pending.toVehicles' },
  station: { to: '/station/devices', label: 'pending.toNetwork' },
} as const;

/**
 * A screen whose data the API does not serve yet. Against the real API it replaces the demo
 * screen, so a real user never sees the demo's numbers as if they were theirs.
 */
export function NotYetLive({ phase, portal }: { phase: 'B3' | 'B4' | 'B5' | 'B6'; portal: keyof typeof LIVE_HOME }) {
  const { t } = useTranslation('live');
  const home = LIVE_HOME[portal];
  return (
    <ListPanel>
      <EmptyState
        icon={Hourglass}
        title={t('pending.title')}
        description={t(`pending.${phase}`)}
        action={
          <Button asChild variant="secondary">
            <Link to={home.to}>{t(home.label)}</Link>
          </Button>
        }
      />
    </ListPanel>
  );
}
