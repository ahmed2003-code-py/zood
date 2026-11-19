/**
 * Typed clients for the master-data endpoints (phase B2). Every type comes from the API's OpenAPI
 * description, so a server change the web has not caught up with fails the typecheck, not a user.
 *
 * These are used by the production screens in src/live only. The in-browser demo keeps its own
 * data (src/mocks) and never calls them.
 */
import type { components } from '@/api/generated/schema';
import { apiRequest } from '@/api/http-client';

type S = components['schemas'];

export type Company = S['CompanyResponse'];
export type CompanyStatus = Company['status'];
export type CompanyProfile = S['CompanyProfile'];
export type Department = S['DepartmentResponse'];
export type Vehicle = S['VehicleResponse'];
export type VehicleDetail = S['VehicleDetail'];
export type VehicleStatus = Vehicle['status'];
export type VehicleTier = Vehicle['tier'];
export type FuelType = Vehicle['fuelType'];
export type Tag = S['TagResponse'];
export type TagStatus = Tag['status'];
export type Limit = S['LimitResponse'];
export type LimitKind = Limit['kind'];
export type LimitPeriod = Limit['period'];
export type Operator = S['OperatorResponse'];
export type OperatorStatus = Operator['status'];
export type Station = S['StationResponse'];
export type StationStatus = Station['status'];
export type Device = S['DeviceResponse'];
export type DeviceActivation = S['DeviceActivationResponse'];
export type Agent = S['AgentResponse'];
export type AgentWithPin = S['AgentPinResponse'];
export type AgentStatus = Agent['status'];
export type Price = S['PriceResponse'];
export type PriceEntry = S['PriceEntryResponse'];
export type AdminUser = S['AdminUserResponse'];
export type UserRole = AdminUser['role'];
export type UserStatus = AdminUser['status'];
export type UserScope = AdminUser['scope'];
export type CreateUserRequest = S['CreateUserRequest'];
export type CreatedUser = S['CreatedUserResponse'];
export type TemporaryPassword = S['TemporaryPasswordResponse'];
export type AuditEntry = S['AuditEntry'];

export type CompanyBody = S['CompanyBody'];
export type CreateCompanyRequest = S['CreateCompanyRequest'];
export type VehicleBody = S['VehicleBody'];
export type StationBody = S['StationBody'];
export type OperatorBody = S['OperatorBody'];
export type ScheduleRequest = S['ScheduleRequest'];

export const USER_ROLES: readonly UserRole[] = ['SUPER_ADMIN', 'OPS', 'FLEET_MANAGER', 'ACCOUNTANT', 'STATION_MANAGER', 'OPS_MANAGER'];
export const FUEL_TYPES: readonly FuelType[] = ['GAS_80', 'GAS_92', 'GAS_95', 'DIESEL', 'CNG'];

// Wallet, top-ups and reconciliation (phase B3). Money is integer piastres.
export type Wallet = S['WalletResponse'];
export type StatementEntry = S['StatementEntryResponse'];
export type TopUp = S['TopUpResponse'];
export type TopUpStatus = TopUp['status'];
export type TopUpRequestBody = S['TopUpRequestBody'];
export type VerifyTopUpBody = S['VerifyTopUpRequest'];
export type RecordTransferBody = S['RecordTransferRequest'];
export type WalletAdjustment = S['WalletAdjustmentResponse'];
export type AdjustmentStatus = WalletAdjustment['status'];
export type Onboarding = S['OnboardingResponse'];
export type OnboardingCheck = S['OnboardingCheck'];
export type FleetImport = S['FleetImportResponse'];
export type FleetImportStatus = FleetImport['status'];
export type FleetImportMode = NonNullable<S['ConfirmFleetImportRequest']['mode']>;
export type FleetImportRow = S['FleetImportRowResponse'];
export type FleetImportRows = S['FleetImportRowsResponse'];
export type FleetImportRowStatus = FleetImportRow['status'];
export type ReconciliationRun = S['ReconciliationRunResponse'];
export type ReconciliationFinding = S['ReconciliationFindingResponse'];

// Fuelings and alerts (phase B4). Money is integer piastres, volume integer millilitres.
export type Fueling = S['FuelingResponse'];
export type DeviceSync = S['DeviceSync'];
export type ActivityDay = S['ActivityDay'];
export type SyncBatch = S['SyncBatch'];
export type FuelingStatus = Fueling['status'];
export type DeclineReason = NonNullable<Fueling['declineReason']>;
export type Alert = S['AlertResponse'];
export type AlertType = Alert['type'];
export type AlertStatus = Alert['status'];
export type AlertSeverity = Alert['severity'];

// Station finance, shifts and tickets (phase B6). Money is integer piastres.
export type StationPayable = S['StationPayableResponse'];
export type StationStatementEntry = S['StationStatementEntryResponse'];
export type StationStatementSummary = S['StationStatementSummaryResponse'];
export type BankAccount = S['BankAccountResponse'];
export type BankAccountStatus = BankAccount['status'];
export type StationBankDetails = S['StationBankDetailsResponse'];
export type BankAccountChangeBody = S['BankAccountChangeBody'];
export type Payout = S['PayoutResponse'];
export type PayoutStatus = Payout['status'];
export type RecordPayoutBody = S['RecordPayoutRequest'];
export type HeldPayable = S['HeldPayableResponse'];
export type HoldState = HeldPayable['state'];
export type Reversal = S['ReversalResponse'];
export type ReversalStatus = Reversal['status'];
export type Shift = S['Shift'];
export type Ticket = S['TicketResponse'];
export type TicketStatus = Ticket['status'];
export type TicketType = Ticket['type'];
export type TicketMessage = S['TicketMessageResponse'];
export type TicketThread = S['TicketThreadResponse'];

/** What a company may open, what a station may open, and every type Operations sees. */
export const COMPANY_TICKET_TYPES: readonly TicketType[] = ['LOST_TAG', 'STOLEN_TAG', 'LIMIT_CHANGE', 'VEHICLE_CHANGE', 'TOPUP_QUERY', 'DISPUTE', 'OTHER'];
export const STATION_TICKET_TYPES: readonly TicketType[] = ['DEVICE_FAULT', 'TAG_READ_ISSUE', 'PAYOUT_QUERY', 'PRICE_QUERY', 'DISPUTE', 'OTHER'];
export const TICKET_TYPES: readonly TicketType[] = [
  'LOST_TAG',
  'STOLEN_TAG',
  'LIMIT_CHANGE',
  'VEHICLE_CHANGE',
  'TOPUP_QUERY',
  'DEVICE_FAULT',
  'TAG_READ_ISSUE',
  'PAYOUT_QUERY',
  'PRICE_QUERY',
  'DISPUTE',
  'OTHER',
];
export const TICKET_STATUSES: readonly TicketStatus[] = ['OPEN', 'IN_PROGRESS', 'RESOLVED', 'CLOSED'];

/** The filters every fueling list shares; `from` and `to` are instants (`to` is exclusive). */
export interface FuelingFilter {
  status?: FuelingStatus;
  from?: string;
  to?: string;
}

/** The list envelope every paginated endpoint returns. */
export interface Page<T> {
  items: T[];
  nextCursor?: string;
}

export interface PageQuery {
  cursor?: string;
  limit?: number;
}

/** Builds a query string from defined, non-empty values only. */
function query(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}

const get = <T>(path: string, signal?: AbortSignal) => apiRequest<T>(path, { signal });
const send = <T>(method: 'POST' | 'PUT' | 'DELETE', path: string, json?: unknown, headers?: Record<string, string>) =>
  apiRequest<T>(path, { method, json, headers });

// ------------------------------------------------------------------------------------- Ops

export const opsApi = {
  companies: {
    list: (q: PageQuery & { status?: CompanyStatus; q?: string }, signal?: AbortSignal) =>
      get<Page<Company>>(`/ops/companies${query({ ...q })}`, signal),
    get: (id: string, signal?: AbortSignal) => get<Company>(`/ops/companies/${id}`, signal),
    /** A repeat with the same key answers the earlier company (200 instead of 201); both are success. */
    create: (body: CreateCompanyRequest, idempotencyKey: string) =>
      send<Company>('POST', '/ops/companies', body, { 'Idempotency-Key': idempotencyKey }),
    onboarding: (id: string, signal?: AbortSignal) => get<Onboarding>(`/ops/companies/${id}/onboarding`, signal),
    update: (id: string, body: CompanyBody) => send<Company>('PUT', `/ops/companies/${id}`, body),
    setPolicy: (id: string, body: S['CompanyPolicyRequest']) => send<Company>('PUT', `/ops/companies/${id}/policy`, body),
    setStatus: (id: string, body: S['CompanyStatusRequest']) => send<Company>('POST', `/ops/companies/${id}/status`, body),
    departments: (id: string, q: PageQuery, signal?: AbortSignal) =>
      get<Page<Department>>(`/ops/companies/${id}/departments${query({ ...q })}`, signal),
    addDepartment: (id: string, body: S['CreateDepartmentRequest']) =>
      send<Department>('POST', `/ops/companies/${id}/departments`, body),
  },
  vehicles: {
    list: (q: PageQuery & { companyId?: string; departmentId?: string; status?: VehicleStatus; q?: string }, signal?: AbortSignal) =>
      get<Page<Vehicle>>(`/ops/vehicles${query({ ...q })}`, signal),
    get: (id: string, signal?: AbortSignal) => get<VehicleDetail>(`/ops/vehicles/${id}`, signal),
    create: (body: S['CreateVehicleRequest']) => send<Vehicle>('POST', '/ops/vehicles', body),
    update: (id: string, body: VehicleBody) => send<Vehicle>('PUT', `/ops/vehicles/${id}`, body),
    setStatus: (id: string, body: S['VehicleStatusRequest']) => send<Vehicle>('POST', `/ops/vehicles/${id}/status`, body),
    issueTag: (id: string, uid: string) => send<Tag>('POST', `/ops/vehicles/${id}/tags`, { uid }),
    setTagStatus: (tagId: string, body: S['TagStatusRequest']) => send<Tag>('POST', `/ops/tags/${tagId}/status`, body),
    setLimit: (id: string, body: S['LimitRequest']) => send<Limit[]>('PUT', `/ops/vehicles/${id}/limits`, body),
    removeLimit: (id: string, limitId: string) => send<void>('DELETE', `/ops/vehicles/${id}/limits/${limitId}`),
  },
  operators: {
    list: (q: PageQuery & { status?: OperatorStatus; q?: string }, signal?: AbortSignal) =>
      get<Page<Operator>>(`/ops/operators${query({ ...q })}`, signal),
    get: (id: string, signal?: AbortSignal) => get<Operator>(`/ops/operators/${id}`, signal),
    create: (body: OperatorBody) => send<Operator>('POST', '/ops/operators', body),
    update: (id: string, body: OperatorBody) => send<Operator>('PUT', `/ops/operators/${id}`, body),
    setStatus: (id: string, body: S['OperatorStatusRequest']) => send<Operator>('POST', `/ops/operators/${id}/status`, body),
  },
  stations: {
    list: (q: PageQuery & { operatorId?: string; status?: StationStatus; q?: string }, signal?: AbortSignal) =>
      get<Page<Station>>(`/ops/stations${query({ ...q })}`, signal),
    get: (id: string, signal?: AbortSignal) => get<Station>(`/ops/stations/${id}`, signal),
    create: (body: S['CreateStationRequest']) => send<Station>('POST', '/ops/stations', body),
    update: (id: string, body: StationBody) => send<Station>('PUT', `/ops/stations/${id}`, body),
    setStatus: (id: string, body: S['StationStatusRequest']) => send<Station>('POST', `/ops/stations/${id}/status`, body),
  },
  devices: {
    list: (q: PageQuery & { operatorId?: string; stationId?: string; status?: Device['status'] }, signal?: AbortSignal) =>
      get<Page<Device>>(`/ops/devices${query({ ...q })}`, signal),
    register: (body: S['RegisterDeviceRequest']) => send<DeviceActivation>('POST', '/ops/devices', body),
    reissueActivation: (id: string) => send<DeviceActivation>('POST', `/ops/devices/${id}/activation-code`),
    revoke: (id: string, reason: string) => send<Device>('POST', `/ops/devices/${id}/revoke`, { reason }),
  },
  agents: {
    list: (q: PageQuery & { operatorId?: string; stationId?: string; status?: AgentStatus; q?: string }, signal?: AbortSignal) =>
      get<Page<Agent>>(`/ops/agents${query({ ...q })}`, signal),
    create: (body: S['CreateAgentRequest']) => send<AgentWithPin>('POST', '/ops/agents', body),
    resetPin: (id: string) => send<AgentWithPin>('POST', `/ops/agents/${id}/reset-pin`),
    setStatus: (id: string, body: S['AgentStatusRequest']) => send<Agent>('POST', `/ops/agents/${id}/status`, body),
  },
  prices: {
    current: (stationId?: string, signal?: AbortSignal) => get<Price[]>(`/ops/prices/current${query({ stationId })}`, signal),
    history: (q: PageQuery & { fuelType?: FuelType; stationId?: string; nationalOnly?: boolean }, signal?: AbortSignal) =>
      get<Page<PriceEntry>>(`/ops/prices${query({ ...q })}`, signal),
    schedule: (body: ScheduleRequest) => send<PriceEntry>('POST', '/ops/prices', body),
  },
  topUps: {
    list: (q: PageQuery & { status?: TopUpStatus; companyId?: string }, signal?: AbortSignal) =>
      get<Page<TopUp>>(`/ops/topups${query({ ...q })}`, signal),
    receivingAccounts: (signal?: AbortSignal) => get<string[]>('/ops/topups/receiving-accounts', signal),
    verify: (id: string, body: VerifyTopUpBody) => send<TopUp>('POST', `/ops/topups/${id}/verify`, body),
    approve: (id: string) => send<TopUp>('POST', `/ops/topups/${id}/approve`),
    reject: (id: string, reason: string) => send<TopUp>('POST', `/ops/topups/${id}/reject`, { reason }),
  },
  wallets: {
    get: (companyId: string, signal?: AbortSignal) => get<Wallet>(`/ops/companies/${companyId}/wallet`, signal),
    statement: (companyId: string, q: PageQuery, signal?: AbortSignal) =>
      get<Page<StatementEntry>>(`/ops/companies/${companyId}/wallet/statement${query({ ...q })}`, signal),
    credit: (companyId: string, body: RecordTransferBody) => send<TopUp>('POST', `/ops/companies/${companyId}/wallet/credits`, body),
    requestAdjustment: (companyId: string, body: S['WalletAdjustmentRequest'], idempotencyKey: string) =>
      send<WalletAdjustment>('POST', `/ops/companies/${companyId}/wallet/adjustments`, body, { 'Idempotency-Key': idempotencyKey }),
  },
  adjustments: {
    list: (q: PageQuery & { status?: AdjustmentStatus; companyId?: string }, signal?: AbortSignal) =>
      get<Page<WalletAdjustment>>(`/ops/wallet-adjustments${query({ ...q })}`, signal),
    approve: (id: string) => send<WalletAdjustment>('POST', `/ops/wallet-adjustments/${id}/approve`),
    reject: (id: string, reason: string) => send<WalletAdjustment>('POST', `/ops/wallet-adjustments/${id}/reject`, { reason }),
    cancel: (id: string) => send<WalletAdjustment>('POST', `/ops/wallet-adjustments/${id}/cancel`),
  },
  fleetImports: {
    list: (q: PageQuery & { companyId?: string }, signal?: AbortSignal) =>
      get<Page<FleetImport>>(`/ops/fleet-imports${query({ ...q })}`, signal),
    get: (id: string, signal?: AbortSignal) => get<FleetImport>(`/ops/fleet-imports/${id}`, signal),
    /** Multipart: the browser sets the Content-Type with its boundary, so none is set here. */
    upload: (companyId: string, file: File) => {
      const form = new FormData();
      form.append('file', file);
      return apiRequest<FleetImport>(`/ops/companies/${companyId}/fleet-imports`, { method: 'POST', body: form });
    },
    rows: (id: string, q: { problemsOnly?: boolean; after?: number; limit?: number }, signal?: AbortSignal) =>
      get<FleetImportRows>(`/ops/fleet-imports/${id}/rows${query({ ...q })}`, signal),
    confirm: (id: string, mode: FleetImportMode) => send<FleetImport>('POST', `/ops/fleet-imports/${id}/confirm`, { mode }),
    cancel: (id: string) => send<FleetImport>('POST', `/ops/fleet-imports/${id}/cancel`),
  },
  fuelings: {
    list: (q: PageQuery & FuelingFilter & { companyId?: string; operatorId?: string; stationId?: string; vehicleId?: string }, signal?: AbortSignal) =>
      get<Page<Fueling>>(`/ops/fuelings${query({ ...q })}`, signal),
    get: (id: string, signal?: AbortSignal) => get<Fueling>(`/ops/fuelings/${id}`, signal),
  },
  activity: (q: { from: string; to: string; companyId?: string; stationId?: string; vehicleId?: string; deviceId?: string; agentId?: string }, signal?: AbortSignal) =>
    get<ActivityDay[]>(`/ops/activity${query({ ...q })}`, signal),
  sync: {
    devices: (q: PageQuery & { operatorId?: string; stationId?: string; stale?: boolean }, signal?: AbortSignal) =>
      get<Page<DeviceSync>>(`/ops/sync/devices${query({ ...q })}`, signal),
    batches: (q: PageQuery & { deviceId?: string }, signal?: AbortSignal) =>
      get<Page<SyncBatch>>(`/ops/sync/batches${query({ ...q })}`, signal),
  },
  alerts: {
    list: (q: PageQuery & { companyId?: string; status?: AlertStatus; type?: AlertType }, signal?: AbortSignal) =>
      get<Page<Alert>>(`/ops/alerts${query({ ...q })}`, signal),
    acknowledge: (id: string) => send<Alert>('POST', `/ops/alerts/${id}/acknowledge`),
  },
  reconciliation: {
    runs: (q: PageQuery, signal?: AbortSignal) => get<Page<ReconciliationRun>>(`/ops/reconciliation/runs${query({ ...q })}`, signal),
    runNow: () => send<ReconciliationRun>('POST', '/ops/reconciliation/runs'),
    findings: (runId: string, q: PageQuery, signal?: AbortSignal) =>
      get<Page<ReconciliationFinding>>(`/ops/reconciliation/runs/${runId}/findings${query({ ...q })}`, signal),
  },
  finance: {
    payable: (stationId: string, signal?: AbortSignal) => get<StationPayable>(`/ops/stations/${stationId}/payable`, signal),
    statement: (stationId: string, q: PageQuery, signal?: AbortSignal) =>
      get<Page<StationStatementEntry>>(`/ops/stations/${stationId}/statement${query({ ...q })}`, signal),
    bankAccount: (stationId: string, signal?: AbortSignal) => get<StationBankDetails>(`/ops/stations/${stationId}/bank-account`, signal),
    requestBankChange: (stationId: string, body: BankAccountChangeBody) =>
      send<BankAccount>('POST', `/ops/stations/${stationId}/bank-account/changes`, body),
    bankChanges: (q: PageQuery & { status?: BankAccountStatus; stationId?: string; operatorId?: string }, signal?: AbortSignal) =>
      get<Page<BankAccount>>(`/ops/bank-account-changes${query({ ...q })}`, signal),
    approveBankChange: (id: string) => send<BankAccount>('POST', `/ops/bank-account-changes/${id}/approve`),
    rejectBankChange: (id: string, reason: string) => send<BankAccount>('POST', `/ops/bank-account-changes/${id}/reject`, { reason }),
    payouts: (q: PageQuery & { stationId?: string; operatorId?: string; status?: PayoutStatus }, signal?: AbortSignal) =>
      get<Page<Payout>>(`/ops/payouts${query({ ...q })}`, signal),
    recordPayout: (stationId: string, body: RecordPayoutBody) => send<Payout>('POST', `/ops/stations/${stationId}/payouts`, body),
    approvePayout: (id: string) => send<Payout>('POST', `/ops/payouts/${id}/approve`),
    cancelPayout: (id: string, reason: string) => send<Payout>('POST', `/ops/payouts/${id}/cancel`, { reason }),
    holds: (q: PageQuery & { stationId?: string; operatorId?: string; state?: HoldState }, signal?: AbortSignal) =>
      get<Page<HeldPayable>>(`/ops/payable-holds${query({ ...q })}`, signal),
    releaseHold: (fuelingId: string, reason: string) =>
      send<HeldPayable>('POST', `/ops/fuelings/${fuelingId}/payable-hold/release`, { reason }),
    reversals: (q: PageQuery & { status?: ReversalStatus; companyId?: string; stationId?: string; fuelingId?: string }, signal?: AbortSignal) =>
      get<Page<Reversal>>(`/ops/reversals${query({ ...q })}`, signal),
    requestReversal: (fuelingId: string, reason: string) => send<Reversal>('POST', `/ops/fuelings/${fuelingId}/reversals`, { reason }),
    approveReversal: (id: string) => send<Reversal>('POST', `/ops/reversals/${id}/approve`),
    rejectReversal: (id: string, reason: string) => send<Reversal>('POST', `/ops/reversals/${id}/reject`, { reason }),
  },
  shifts: (q: PageQuery & { operatorId?: string; stationId?: string; deviceId?: string; agentId?: string }, signal?: AbortSignal) =>
    get<Page<Shift>>(`/ops/shifts${query({ ...q })}`, signal),
  tickets: {
    list: (q: PageQuery & { companyId?: string; operatorId?: string; status?: TicketStatus; type?: TicketType }, signal?: AbortSignal) =>
      get<Page<Ticket>>(`/ops/tickets${query({ ...q })}`, signal),
    get: (id: string, signal?: AbortSignal) => get<TicketThread>(`/ops/tickets/${id}`, signal),
    reply: (id: string, body: string, internal: boolean) => send<TicketThread>('POST', `/ops/tickets/${id}/messages`, { body, internal }),
    setStatus: (id: string, status: TicketStatus, assignToMe?: boolean) =>
      send<TicketThread>('POST', `/ops/tickets/${id}/status`, { status, assignToMe }),
  },
};

// ----------------------------------------------------------------------------------- admin

export const adminApi = {
  users: {
    list: (q: PageQuery & { role?: UserRole; status?: UserStatus; q?: string }, signal?: AbortSignal) =>
      get<Page<AdminUser>>(`/admin/users${query({ ...q })}`, signal),
    create: (body: CreateUserRequest) => send<CreatedUser>('POST', '/admin/users', body),
    resetPassword: (id: string) => send<TemporaryPassword>('POST', `/admin/users/${id}/reset-password`),
    unlock: (id: string) => send<void>('POST', `/admin/users/${id}/unlock`),
    setStatus: (id: string, body: S['StatusRequest']) => send<void>('POST', `/admin/users/${id}/status`, body),
    resetMfa: (id: string, reason: string) => send<void>('POST', `/admin/users/${id}/mfa/reset`, { reason }),
  },
  audit: {
    list: (q: PageQuery, signal?: AbortSignal) => get<Page<AuditEntry>>(`/admin/audit${query({ ...q })}`, signal),
  },
};

// ---------------------------------------------------------------------------- company portal

export const companyApi = {
  profile: (signal?: AbortSignal) => get<CompanyProfile>('/company/profile', signal),
  departments: (q: PageQuery, signal?: AbortSignal) => get<Page<Department>>(`/company/departments${query({ ...q })}`, signal),
  vehicles: (q: PageQuery & { departmentId?: string; status?: VehicleStatus; q?: string }, signal?: AbortSignal) =>
    get<Page<Vehicle>>(`/company/vehicles${query({ ...q })}`, signal),
  vehicle: (id: string, signal?: AbortSignal) => get<VehicleDetail>(`/company/vehicles/${id}`, signal),
  wallet: (signal?: AbortSignal) => get<Wallet>('/company/wallet', signal),
  statement: (q: PageQuery, signal?: AbortSignal) => get<Page<StatementEntry>>(`/company/wallet/statement${query({ ...q })}`, signal),
  topUps: (q: PageQuery & { status?: TopUpStatus }, signal?: AbortSignal) =>
    get<Page<TopUp>>(`/company/topups${query({ ...q })}`, signal),
  requestTopUp: (body: TopUpRequestBody) => send<TopUp>('POST', '/company/topups', body),
  cancelTopUp: (id: string) => send<TopUp>('POST', `/company/topups/${id}/cancel`),
  fuelings: (q: PageQuery & FuelingFilter & { vehicleId?: string }, signal?: AbortSignal) =>
    get<Page<Fueling>>(`/company/fuelings${query({ ...q })}`, signal),
  fueling: (id: string, signal?: AbortSignal) => get<Fueling>(`/company/fuelings/${id}`, signal),
  alerts: (q: PageQuery & { status?: AlertStatus }, signal?: AbortSignal) =>
    get<Page<Alert>>(`/company/alerts${query({ ...q })}`, signal),
  activity: (q: { from: string; to: string; vehicleId?: string }, signal?: AbortSignal) =>
    get<ActivityDay[]>(`/company/activity${query({ ...q })}`, signal),
  reversals: (q: PageQuery, signal?: AbortSignal) => get<Page<Reversal>>(`/company/reversals${query({ ...q })}`, signal),
  tickets: {
    list: (q: PageQuery & { status?: TicketStatus }, signal?: AbortSignal) => get<Page<Ticket>>(`/company/tickets${query({ ...q })}`, signal),
    get: (id: string, signal?: AbortSignal) => get<TicketThread>(`/company/tickets/${id}`, signal),
    open: (body: S['OpenTicketRequest']) => send<TicketThread>('POST', '/company/tickets', body),
    reply: (id: string, body: string) => send<TicketThread>('POST', `/company/tickets/${id}/messages`, { body }),
  },
};

// ---------------------------------------------------------------------------- station portal

export const stationApi = {
  operator: (signal?: AbortSignal) => get<Operator>('/station/operator', signal),
  stations: (q: PageQuery & { status?: StationStatus; q?: string }, signal?: AbortSignal) =>
    get<Page<Station>>(`/station/stations${query({ ...q })}`, signal),
  station: (id: string, signal?: AbortSignal) => get<Station>(`/station/stations/${id}`, signal),
  devices: (q: PageQuery & { stationId?: string; status?: Device['status'] }, signal?: AbortSignal) =>
    get<Page<Device>>(`/station/devices${query({ ...q })}`, signal),
  agents: (q: PageQuery & { stationId?: string; status?: AgentStatus; q?: string }, signal?: AbortSignal) =>
    get<Page<Agent>>(`/station/agents${query({ ...q })}`, signal),
  fuelings: (q: PageQuery & FuelingFilter & { stationId?: string }, signal?: AbortSignal) =>
    get<Page<Fueling>>(`/station/fuelings${query({ ...q })}`, signal),
  fueling: (id: string, signal?: AbortSignal) => get<Fueling>(`/station/fuelings/${id}`, signal),
  activity: (q: { from: string; to: string; stationId?: string; deviceId?: string; agentId?: string }, signal?: AbortSignal) =>
    get<ActivityDay[]>(`/station/activity${query({ ...q })}`, signal),
  payable: (stationId: string, signal?: AbortSignal) => get<StationPayable>(`/station/stations/${stationId}/payable`, signal),
  statement: (stationId: string, q: PageQuery, signal?: AbortSignal) =>
    get<Page<StationStatementEntry>>(`/station/stations/${stationId}/statement${query({ ...q })}`, signal),
  bankAccount: (stationId: string, signal?: AbortSignal) => get<StationBankDetails>(`/station/stations/${stationId}/bank-account`, signal),
  payouts: (q: PageQuery & { stationId?: string }, signal?: AbortSignal) => get<Page<Payout>>(`/station/payouts${query({ ...q })}`, signal),
  reversals: (q: PageQuery & { stationId?: string }, signal?: AbortSignal) =>
    get<Page<Reversal>>(`/station/reversals${query({ ...q })}`, signal),
  shifts: (q: PageQuery & { stationId?: string; deviceId?: string; agentId?: string }, signal?: AbortSignal) =>
    get<Page<Shift>>(`/station/shifts${query({ ...q })}`, signal),
  tickets: {
    list: (q: PageQuery & { status?: TicketStatus }, signal?: AbortSignal) => get<Page<Ticket>>(`/station/tickets${query({ ...q })}`, signal),
    get: (id: string, signal?: AbortSignal) => get<TicketThread>(`/station/tickets/${id}`, signal),
    open: (body: S['OpenTicketRequest']) => send<TicketThread>('POST', '/station/tickets', body),
    reply: (id: string, body: string) => send<TicketThread>('POST', `/station/tickets/${id}/messages`, { body }),
  },
};
