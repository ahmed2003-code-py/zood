/**
 * Station-portal calls not in the shared client (src/live/api.ts), kept here so the shared file
 * stays as it is. Same endpoints, same types from the OpenAPI description.
 */
import { apiRequest } from '@/api/http-client';
import type { Page, PageQuery, StationStatementEntry, StationStatementSummary } from '../api';

const search = (params: Record<string, string | number | undefined>) => {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value !== undefined && value !== '') query.set(key, String(value));
  const text = query.toString();
  return text ? `?${text}` : '';
};

/** Opening and closing balance, credits and debits between two instants (ISO, the upper bound exclusive). */
export const statementSummary = (stationId: string, range: { from?: string; to?: string }, signal?: AbortSignal) =>
  apiRequest<StationStatementSummary>(`/station/stations/${stationId}/statement/summary${search(range)}`, { signal });

/** One page of the statement, newest first. */
export const statementPage = (stationId: string, page: PageQuery, signal?: AbortSignal) =>
  apiRequest<Page<StationStatementEntry>>(`/station/stations/${stationId}/statement${search({ cursor: page.cursor, limit: page.limit })}`, { signal });
