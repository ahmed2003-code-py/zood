/**
 * The parts of the ZAWED mark the sign-in stage animates: the fuel drop and the three speed lines.
 * Copied from the generated logo geometry (src/design-system/brand/logo-geometry.ts) so this small
 * public chunk does not pull the design-system bundle into the app's entry; marks.test.ts keeps
 * the copy equal to the source.
 */
export const DROP_PATH = 'M90.12 -37.49C92.81 -32.45 98.52 -27.41 98.52 -22.87A8.4 8.4 0 0 1 81.72 -22.87C81.72 -27.41 87.43 -32.45 90.12 -37.49Z';
export const DROP_HIGHLIGHT = 'M85.75 -23.71Q85.92 -18.67 90.29 -17.83';
/** Widths of the logo's three speed lines, longest first. */
export const SPEED_LINE_WIDTHS = [40, 31, 23] as const;
