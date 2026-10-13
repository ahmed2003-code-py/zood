import { Clock, Star, Undo2, Wifi, WifiOff } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { FleetTier, TransactionStatus } from '@/domain/types';
import { StatusChip } from './StatusChip';
import type { Tone } from './tone';
import type { LucideIcon } from './Icon';

const TRANSACTION_TONE: Record<TransactionStatus, { tone: Tone; icon?: LucideIcon }> = {
  completed: { tone: 'success' },
  declined: { tone: 'danger' },
  pendingSync: { tone: 'warning', icon: Clock },
  reversed: { tone: 'neutral', icon: Undo2 },
};

export function TransactionStatusChip({ status, size }: { status: TransactionStatus; size?: 'sm' | 'md' }) {
  const { t } = useTranslation('status');
  const { tone, icon } = TRANSACTION_TONE[status];
  return <StatusChip tone={tone} icon={icon} size={size} label={t(`transaction.${status}`)} />;
}

export function TierChip({ tier }: { tier: FleetTier }) {
  const { t } = useTranslation('status');
  if (tier === 'vip') return <StatusChip tone="brand" icon={Star} label={t('tier.vip')} />;
  return <StatusChip tone="neutral" label={t('tier.normal')} />;
}

export function ConnectivityChip({ online }: { online: boolean }) {
  const { t } = useTranslation('status');
  return (
    <StatusChip
      tone={online ? 'success' : 'warning'}
      icon={online ? Wifi : WifiOff}
      label={t(online ? 'connectivity.online' : 'connectivity.offline')}
    />
  );
}
