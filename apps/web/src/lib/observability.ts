/**
 * Error reporting (B7, owner decision 2026-11-19: Sentry, without personal or financial data).
 *
 * Off unless VITE_SENTRY_DSN is set at build time. When it is off, the SDK is never downloaded: it
 * is imported on demand. What is sent is reduced to what a fix needs: the error type, message,
 * stack and the page's route pattern. Request data, the user, query strings, URL ids and
 * breadcrumbs (console lines, clicks, fetch URLs) are removed, because they carry plates, names,
 * amounts and ids. No session replay, no performance tracing.
 */

/** A route with ids and query removed: /ops/companies/3f2c…/wallet?tab=x → /ops/companies/:id/wallet */
export function scrubUrl(url: string): string {
  let path: string;
  try {
    path = new URL(url, 'http://local').pathname;
  } catch {
    path = url.split(/[?#]/)[0] ?? '';
  }
  return path
    .split('/')
    .map((segment) =>
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(segment) || /\d/.test(segment) ? ':id' : segment,
    )
    .join('/');
}

interface ScrubbableEvent {
  request?: unknown;
  user?: unknown;
  breadcrumbs?: unknown[];
  extra?: unknown;
  tags?: Record<string, unknown>;
}

/** Applied to every event before it is sent. Exported for its test. */
export function scrubEvent<T extends ScrubbableEvent>(event: T, route: string): T {
  delete event.request;
  delete event.user;
  delete event.extra;
  event.breadcrumbs = [];
  event.tags = { ...event.tags, route };
  return event;
}

export function initObservability(): void {
  const dsn = import.meta.env.VITE_SENTRY_DSN;
  if (!dsn) return;
  // @sentry/browser, not @sentry/react: no React feature is needed, and importing React from the
  // lazy SDK chunk made Rollup fold the shared design-system chunk (≈340 KiB) into the entry chunk.
  void import('@sentry/browser').then((Sentry) => {
    Sentry.init({
      dsn,
      environment: import.meta.env.VITE_SENTRY_ENVIRONMENT ?? import.meta.env.MODE,
      release: import.meta.env.VITE_SENTRY_RELEASE,
      // Sentry 11 collects all of these by default; every category is switched off.
      dataCollection: { userInfo: false, cookies: false, httpHeaders: false, httpBodies: [], urlQueryParams: false },
      tracesSampleRate: 0,
      maxBreadcrumbs: 0,
      // Only the default error integrations: no replay, no tracing, no console capture.
      integrations: (defaults) => defaults.filter((integration) => integration.name !== 'Breadcrumbs'),
      beforeSend: (event) => scrubEvent(event, scrubUrl(window.location.href)),
    });
  });
}
