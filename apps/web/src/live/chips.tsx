import { useTranslation } from 'react-i18next';
import { StatusChip, type Tone } from '@/design-system';
import type { AgentStatus, CompanyStatus, Device, OperatorStatus, StationStatus, TagStatus, UserStatus, VehicleStatus } from './api';

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
