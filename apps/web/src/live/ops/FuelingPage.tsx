import { Undo2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, toast } from '@/design-system';
import { opsApi, type Fueling } from '../api';
import { ReversalStatusChip } from '../chips';
import { ReasonSheet } from '../components';
import { FuelingDetailPage } from '../fuelings';
import { useResource } from '../use-resource';

/**
 * Ops: the reversal of one completed, charged fueling. A request goes to a second person in Ops;
 * the page shows the request that already exists, so the same fueling is not asked about twice.
 */
function ReversalAction({ fueling }: { fueling: Fueling }) {
  const { t } = useTranslation('live');
  const [open, setOpen] = useState(false);
  const existing = useResource((signal) => opsApi.finance.reversals({ fuelingId: fueling.id, limit: 1 }, signal), [fueling.id]);
  const latest = existing.data?.items[0];
  // A pending or approved reversal is final for this fueling; a rejected one may be asked again.
  const blocked = latest && latest.status !== 'REJECTED' ? latest : undefined;
  if (fueling.status !== 'COMPLETED' || !fueling.billable) return null;
  return (
    <>
      {blocked ? (
        <Link to="/ops/settlements" className="flex min-h-11 items-center gap-2 text-sm text-primary hover:underline">
          {t('settlements.reversals.existing')}
          <ReversalStatusChip status={blocked.status} />
        </Link>
      ) : (
        <Button variant="secondary" leadingIcon={Undo2} onClick={() => setOpen(true)}>
          {t('settlements.reversals.request')}
        </Button>
      )}
      <ReasonSheet
        open={open}
        onOpenChange={setOpen}
        title={t('settlements.reversals.requestTitle')}
        description={t('settlements.reversals.requestHint')}
        submitLabel={t('settlements.reversals.request')}
        submitVariant="danger"
        onConfirm={async (reason) => {
          await opsApi.finance.requestReversal(fueling.id, reason);
          toast(t('settlements.reversals.requested'));
          existing.reload();
        }}
      />
    </>
  );
}

/** Ops: one fueling with the records behind it. */
export function FuelingPage() {
  return (
    <FuelingDetailPage
      portal="ops"
      backTo="/ops/transactions"
      load={(id, signal) => opsApi.fuelings.get(id, signal)}
      renderActions={(fueling) => <ReversalAction key={fueling.id} fueling={fueling} />}
    />
  );
}
