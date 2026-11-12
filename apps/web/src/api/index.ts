import { httpApi } from './http-api';
import { mockApi } from './mock-api';
import type { ZoodApi } from './types';

export type ApiMode = 'mock' | 'http';

/**
 * Where screens get their data.
 * - `mock` (default): demo data in the browser. This is the investor demo and needs no server.
 * - `http`: the ZAWED API. Set VITE_API_MODE=http when starting or building the web app.
 */
export const API_MODE: ApiMode = import.meta.env.VITE_API_MODE === 'http' ? 'http' : 'mock';

/** The single API entry point for screens. Screens never import an implementation directly. */
export const api: ZoodApi = API_MODE === 'http' ? httpApi : mockApi;

export type { TransactionQuery, ZoodApi } from './types';
