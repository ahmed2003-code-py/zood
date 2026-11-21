import { describe, expect, it } from 'vitest';
import caddyfile from '../../Caddyfile?raw';
import html from '../../index.html?raw';
import robots from '../../public/robots.txt?raw';
import sitemap from '../../public/sitemap.xml?raw';
import { SITE_ORIGIN, SITEMAP_PATHS } from './site';

describe('crawler files', () => {
  it('index.html carries an Arabic description and a complete share card', () => {
    expect(html).toMatch(/<meta name="description" content="[^"]*زوّد[^"]*"/);
    for (const property of ['og:title', 'og:description', 'og:image', 'og:url', 'og:type']) {
      expect(html).toContain(`property="${property}"`);
    }
    expect(html).toContain('name="twitter:card" content="summary_large_image"');
    expect(html).toContain(`content="${SITE_ORIGIN}/og-card.png"`);
  });

  it('robots.txt points at the sitemap and keeps the portals out', () => {
    expect(robots).toContain(`Sitemap: ${SITE_ORIGIN}/sitemap.xml`);
    for (const path of ['/company/', '/station/', '/ops/', '/pos/', '/login']) expect(robots).toContain(`Disallow: ${path}`);
  });

  it('the sitemap lists every public page on the same origin', () => {
    for (const path of SITEMAP_PATHS) expect(sitemap).toContain(`<loc>${SITE_ORIGIN}${path}</loc>`);
  });

  it('the share image exists', () => {
    expect(Object.keys(import.meta.glob('../../public/og-card.png'))).toHaveLength(1);
  });

  /*
   * Caddy serves a file when it exists and only then falls back to the SPA, so /robots.txt and
   * /sitemap.xml reach crawlers as themselves. This fails if that order is ever changed.
   */
  it('the web server serves real files before the single-page fallback', () => {
    expect(caddyfile).toMatch(/try_files \{path\} \/index\.html/);
  });
});
