import type { components } from './generated/schema';
import { apiRequest } from './http-client';

/** What the API reports about itself: service, version, contract version, schema version. */
export type ServiceMeta = components['schemas']['MetaResponse'];

/**
 * The contract version this web build was written for. When the API reports a higher number, an
 * existing client can no longer rely on the endpoints it knows.
 */
export const SUPPORTED_CONTRACT_VERSION = 1;

/** Service-level endpoints that exist only on the real API (there is no mock for them). */
export const platformApi = {
  meta: (signal?: AbortSignal): Promise<ServiceMeta> => apiRequest<ServiceMeta>('/meta', { signal }),
};
