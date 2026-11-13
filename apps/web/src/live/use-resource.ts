/**
 * Loading data from the API for a screen: the four states every data screen has (loading, error,
 * empty, loaded) without a caching library.
 *
 * Each answer is stored with the request it belongs to, so "loading" simply means the stored answer
 * is for an older request. A newer request cancels the older one, so a fast typist in a search box
 * never sees a stale answer land last.
 */
import { useCallback, useEffect, useEffectEvent, useState } from 'react';
import type { Page, PageQuery } from './api';

export interface Resource<T> {
  data: T | undefined;
  error: unknown;
  loading: boolean;
  reload: () => void;
}

type Settled<T> = { key: string; data?: T; error?: unknown };

/** `deps` are the request's inputs (ids, filters): plain strings, numbers, booleans or undefined. */
export function useResource<T>(load: (signal: AbortSignal) => Promise<T>, deps: readonly unknown[]): Resource<T> {
  const [version, setVersion] = useState(0);
  const key = JSON.stringify([deps, version]);
  const [settled, setSettled] = useState<Settled<T>>();
  const run = useEffectEvent(load);

  useEffect(() => {
    const controller = new AbortController();
    run(controller.signal).then(
      (data) => {
        if (!controller.signal.aborted) setSettled({ key, data });
      },
      (error: unknown) => {
        if (!controller.signal.aborted) setSettled({ key, error });
      },
    );
    return () => controller.abort();
  }, [key]);

  const reload = useCallback(() => setVersion((value) => value + 1), []);
  const current = settled?.key === key;
  return {
    // Keep showing the previous data while a reload is on its way, so the screen does not flash.
    data: settled?.data,
    error: current ? settled?.error : undefined,
    loading: !current,
    reload,
  };
}

export interface PagedResource<T> {
  items: T[];
  error: unknown;
  /** The first page is loading. */
  loading: boolean;
  /** A further page is loading. */
  loadingMore: boolean;
  hasMore: boolean;
  loadMore: () => void;
  reload: () => void;
}

type PagedState<T> = { key: string; items: T[]; nextCursor?: string; error?: unknown };

/** A cursor-paginated list with "load more". Changing `deps` (filters, search) starts again from page one. */
export function usePagedResource<T>(
  load: (page: PageQuery, signal: AbortSignal) => Promise<Page<T>>,
  deps: readonly unknown[],
  limit = 25,
): PagedResource<T> {
  const [version, setVersion] = useState(0);
  const key = JSON.stringify([deps, version, limit]);
  const [state, setState] = useState<PagedState<T>>();
  const [requested, setRequested] = useState<{ key: string; cursor: string }>();
  const run = useEffectEvent(load);

  // First page, whenever the query changes.
  useEffect(() => {
    const controller = new AbortController();
    run({ limit }, controller.signal).then(
      (page) => {
        if (!controller.signal.aborted) setState({ key, items: page.items, nextCursor: page.nextCursor ?? undefined });
      },
      (error: unknown) => {
        if (!controller.signal.aborted) setState({ key, items: [], error });
      },
    );
    return () => controller.abort();
  }, [key, limit]);

  // A further page, when asked for.
  useEffect(() => {
    if (!requested || requested.key !== key) return;
    const controller = new AbortController();
    run({ cursor: requested.cursor, limit }, controller.signal).then(
      (page) => {
        if (controller.signal.aborted) return;
        setState((previous) =>
          previous && previous.key === key && previous.nextCursor === requested.cursor
            ? { key, items: [...previous.items, ...page.items], nextCursor: page.nextCursor ?? undefined }
            : previous,
        );
      },
      (error: unknown) => {
        if (!controller.signal.aborted) setState((previous) => (previous ? { ...previous, error } : previous));
      },
    );
    return () => controller.abort();
  }, [requested, key, limit]);

  const current = state?.key === key ? state : undefined;
  const nextCursor = current?.nextCursor;
  const loadingMore = Boolean(nextCursor && requested?.key === key && requested.cursor === nextCursor && !current?.error);

  const loadMore = useCallback(() => {
    if (nextCursor) setRequested({ key, cursor: nextCursor });
  }, [key, nextCursor]);
  const reload = useCallback(() => setVersion((value) => value + 1), []);

  return {
    items: current?.items ?? [],
    error: current?.error,
    loading: !current,
    loadingMore,
    hasMore: Boolean(nextCursor),
    loadMore,
    reload,
  };
}

/** A search box's value, settled: the request goes out once typing pauses. */
export function useDebounced<T>(value: T, delayMs = 300): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(value), delayMs);
    return () => window.clearTimeout(timer);
  }, [value, delayMs]);
  return settled;
}
