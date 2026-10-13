import { mockApi } from './mock-api';
import type { ZoodApi } from './types';

/** The single API entry point for screens. Swap for the HTTP client when the backend exists. */
export const api: ZoodApi = mockApi;

export type { TransactionQuery, ZoodApi } from './types';
