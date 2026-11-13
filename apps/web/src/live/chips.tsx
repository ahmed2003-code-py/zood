import { useTranslation } from 'react-i18next';
import { StatusChip, type Tone } from '@/design-system';
import type {
  AgentStatus,
  AlertSeverity,
  AlertStatus,
  CompanyStatus,
  DeclineReason,
  Device,
  FuelingStatus,
  OperatorStatus,
  StationStatus,
  TagStatus,
  TopUpStatus,
  UserStatus,
  VehicleStatus,
} from './api';

const COMPANY_TONE: Record<CompanyStatus, Tone> = { ACTIVE: 'success', FROZEN: 'warning' };
const VEHICLE_TONE: Record<VehicleStatus, Tone> = { ACTIVE: 'success', SUSPENDED: 'warning', DECOMMISSIONED: 'neutral' };
const TAG_TONE: Record<TagStatus, Tone> = {
  ACTIVE: 'success',
  REPLACEMENT_PENDING: 'warning',
  LOST: 'danger',
  STOLEN: 'danger',
  DEACTIVATED: 'neutral',
};

export function CompanyStatusChip({ status }: { status: CompanyStatus }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={COMPANY_TONE[status]} label={t(`enums.companyStatus.${status}`)} />;
}

export function VehicleStatusChip({ status }: { status: VehicleStatus }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={VEHICLE_TONE[status]} label={t(`enums.vehicleStatus.${status}`)} />;
}

const TOPUP_TONE: Record<TopUpStatus, Tone> = { PENDING: 'warning', CONFIRMED: 'success', REJECTED: 'danger', CANCELLED: 'neutral' };

export function TopUpStatusChip({ status }: { status: TopUpStatus }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={TOPUP_TONE[status]} label={t(`topUps.status.${status}`)} />;
}

export function TagStatusChip({ status }: { status: TagStatus }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={TAG_TONE[status]} label={t(`enums.tagStatus.${status}`)} />;
}

const STATION_TONE: Record<StationStatus, Tone> = { ACTIVE: 'success', SUSPENDED: 'warning', CLOSED: 'neutral' };
const OPERATOR_TONE: Record<OperatorStatus, Tone> = { ACTIVE: 'success', SUSPENDED: 'warning' };
const DEVICE_TONE: Record<Device['status'], Tone> = { ACTIVE: 'success', PENDING_ACTIVATION: 'warning', REVOKED: 'neutral' };
const AGENT_TONE: Record<AgentStatus, Tone> = { ACTIVE: 'success', SUSPENDED: 'warning', DEACTIVATED: 'neutral' };

export function StationStatusChip({ status }: { status: StationStatus }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={STATION_TONE[status]} label={t(`enums.stationStatus.${status}`)} />;
}

export function OperatorStatusChip({ status }: { status: OperatorStatus }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={OPERATOR_TONE[status]} label={t(`enums.operatorStatus.${status}`)} />;
}

export function DeviceStatusChip({ status }: { status: Device['status'] }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={DEVICE_TONE[status]} label={t(`enums.deviceStatus.${status}`)} />;
}

export function AgentStatusChip({ status }: { status: AgentStatus }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={AGENT_TONE[status]} label={t(`enums.agentStatus.${status}`)} />;
}

const USER_TONE: Record<UserStatus, Tone> = { ACTIVE: 'success', SUSPENDED: 'warning', DEACTIVATED: 'neutral' };

export function UserStatusChip({ status }: { status: UserStatus }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={USER_TONE[status]} label={t(`enums.userStatus.${status}`)} />;
}

/** A completed fueling, or a declined one with the rule that stopped it. */
export function FuelingStatusChip({ status, declineReason }: { status: FuelingStatus; declineReason?: DeclineReason }) {
  const { t } = useTranslation('live');
  if (status === 'COMPLETED') return <StatusChip tone="success" label={t('fuelings.status.COMPLETED')} />;
  return (
    <StatusChip
      tone="danger"
      label={declineReason ? t('fuelings.declinedWith', { reason: t(`fuelings.decline.${declineReason}`) }) : t('fuelings.status.DECLINED')}
    />
  );
}

const SEVERITY_TONE: Record<AlertSeverity, Tone> = { WARNING: 'warning', CRITICAL: 'danger' };
const ALERT_STATUS_TONE: Record<AlertStatus, Tone> = { OPEN: 'warning', ACKNOWLEDGED: 'neutral' };

export function AlertSeverityChip({ severity }: { severity: AlertSeverity }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={SEVERITY_TONE[severity]} label={t(`alerts.severity.${severity}`)} />;
}

export function AlertStatusChip({ status }: { status: AlertStatus }) {
  const { t } = useTranslation('live');
  return <StatusChip tone={ALERT_STATUS_TONE[status]} label={t(`alerts.status.${status}`)} />;
}
