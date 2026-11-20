import { Compass } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, EmptyState } from '@/design-system';
import { ListPanel } from './page';
import type { PortalConfig } from './types';

/**
 * An unknown address inside a portal (/ops/…, /company/…): said plainly, with the way home, instead
 * of a silent redirect that leaves the person wondering where their link went.
 */
export function PortalNotFound({ config }: { config: PortalConfig }) {
  const { t } = useTranslation('portal');
  return (
    <div className="mx-auto max-w-xl pt-6 md:pt-12">
      <h1 className="sr-only">{t('notFound.title')}</h1>
      <ListPanel>
        <EmptyState
          icon={Compass}
          title={t('notFound.title')}
          description={t('notFound.description')}
          action={
            <Button asChild>
              <Link to={config.basePath}>{t('notFound.home')}</Link>
            </Button>
          }
        />
      </ListPanel>
    </div>
  );
}
