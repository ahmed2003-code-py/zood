/**
 * Where the brand panel sits in the sign-in grid (AuthLayout), shared by the panel and by the
 * placeholder shown while its chunk loads, so nothing moves when it arrives.
 * Phones: a band under the header (rows 1–2). From lg: the inline-end column, full height, inset.
 */
export const PANEL_OUTER = 'col-start-1 row-span-2 row-start-1 lg:sticky lg:top-0 lg:col-start-2 lg:row-span-3 lg:row-start-1 lg:h-dvh lg:p-3';
export const PANEL_INNER = 'relative isolate h-full min-h-[14.5rem] overflow-hidden bg-stage text-stage-foreground lg:rounded-[1.75rem]';
