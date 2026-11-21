/** Page sections reachable from the header and the footer, in page order (ids double as anchors). */
export const SECTION_LINKS = ['how', 'fleets', 'stations', 'trust', 'pricing', 'faq'] as const;
export type SectionLink = (typeof SECTION_LINKS)[number];
