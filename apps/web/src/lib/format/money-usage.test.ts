import { describe, expect, it } from 'vitest';

/**
 * One money format in the portals (QA round 1): amounts are shown to the piastre, never rounded to
 * whole pounds. Whole pounds stay for the public price list and chart axes only.
 */
const sources = import.meta.glob(['/src/live/**/*.tsx', '/src/portals/**/*.tsx', '/src/shell/**/*.tsx', '/src/features/**/*.tsx'], {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>;

describe('money in the portals', () => {
  it('is never rounded to whole pounds', () => {
    const offenders = Object.entries(sources)
      .filter(([, source]) => /precision(=["']whole["']|:\s*'whole')/.test(source))
      .map(([path]) => path);
    expect(offenders).toEqual([]);
  });

  it('covers the portal sources', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(50);
  });
});
