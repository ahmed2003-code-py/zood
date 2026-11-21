/**
 * The public site's address, used by the static share tags in index.html, public/robots.txt and
 * public/sitemap.xml (crawlers need absolute URLs). PLACEHOLDER: this is the zawed-dev web service;
 * replace it in all four places when the owner's own domain is live. site.test.ts keeps them equal.
 */
export const SITE_ORIGIN = 'https://zawed-web-production.up.railway.app';

/** Public pages listed in the sitemap. */
export const SITEMAP_PATHS = ['/', '/privacy', '/terms'] as const;
