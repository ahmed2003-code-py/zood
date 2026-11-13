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

export type CompanyBody = S['CompanyBody'];
export type CreateCompanyRequest = S['CreateCompanyRequest'];
export type VehicleBody = S['VehicleBody'];
export type StationBody = S['StationBody'];
export type OperatorBody = S['OperatorBody'];
export type ScheduleRequest = S['ScheduleRequest'];

export const FUEL_TYPES: readonly FuelType[] = ['GAS_80', 'GAS_92', 'GAS_95', 'DIESEL', 'CNG'];

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
const send = <T>(method: 'POST' | 'PUT' | 'DELETE', path: string, json?: unknown) =>
  apiRequest<T>(path, { method, json });

// ------------------------------------------------------------------------------------- Ops

export const opsApi = {
  companies: {
    list: (q: PageQuery & { status?: CompanyStatus; q?: string }, signal?: AbortSignal) =>
      get<Page<Company>>(`/ops/companies${query({ ...q })}`, signal),
    get: (id: string, signal?: AbortSignal) => get<Company>(`/ops/companies/${id}`, signal),
    create: (body: CreateCompanyRequest) => send<Company>('POST', '/ops/companies', body),
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
};

// ---------------------------------------------------------------------------- company portal

export const companyApi = {
  profile: (signal?: AbortSignal) => get<CompanyProfile>('/company/profile', signal),
  departments: (q: PageQuery, signal?: AbortSignal) => get<Page<Department>>(`/company/departments${query({ ...q })}`, signal),
  vehicles: (q: PageQuery & { departmentId?: string; status?: VehicleStatus; q?: string }, signal?: AbortSignal) =>
    get<Page<Vehicle>>(`/company/vehicles${query({ ...q })}`, signal),
  vehicle: (id: string, signal?: AbortSignal) => get<VehicleDetail>(`/company/vehicles/${id}`, signal),
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
};
