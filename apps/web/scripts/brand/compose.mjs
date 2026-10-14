// Composes ZAWED logo variants from raw.json (glyph outlines) and writes:
//  - <web>/src/design-system/brand/logo-geometry.ts  (shared geometry for the React component)
//  - <repo>/docs/brand/*.svg and <web>/public/favicon.svg (static assets with token colors)
//  - preview.html (visual check)
import fs from 'node:fs';
import path from 'node:path';

const web = process.argv[2];
const repo = path.resolve(web, '../..');
const raw = JSON.parse(fs.readFileSync('raw.json', 'utf8'));
const r2 = (value) => Math.round(value * 100) / 100;

// Token values (src/styles/tokens.css) used for static assets only. The React component uses the tokens themselves.
const TOKENS = {
  light: { mark: '#0F3B2C', accent: '#F28C28', surface: '#F7F5F0' }, // --brand-mark, --accent, --background
  dark: { mark: '#EEEAE2', accent: '#F5A04A', surface: '#0C1411' }, // dark --brand-mark, --accent, --background
  inverse: { mark: '#FFFFFF', accent: '#F28C28', surface: '#0F3B2C' }, // --sidebar-active-foreground on --sidebar/--hero
};

// ---- Arabic wordmark geometry
const ar = raw.ar;
const counter = ar.counterBox;
const drop = (() => {
  const cx = r2((counter.minX + counter.maxX) / 2);
  const cy = r2((counter.minY + counter.maxY) / 2 + 1.5);
  const r = 8.4;
  const top = r2(cy - 1.62 * r);
  const d = [
    `M${cx} ${top}`,
    `C${r2(cx + 0.32 * r)} ${r2(cy - 1.02 * r)} ${r2(cx + r)} ${r2(cy - 0.42 * r)} ${r2(cx + r)} ${r2(cy + 0.12 * r)}`,
    `A${r} ${r} 0 0 1 ${r2(cx - r)} ${r2(cy + 0.12 * r)}`,
    `C${r2(cx - r)} ${r2(cy - 0.42 * r)} ${r2(cx - 0.32 * r)} ${r2(cy - 1.02 * r)} ${cx} ${top}Z`,
  ].join('');
  // Small accent reflection inside the drop (lower-left), echoing the reference.
  const highlight = `M${r2(cx - 0.52 * r)} ${r2(cy + 0.02 * r)}Q${r2(cx - 0.5 * r)} ${r2(cy + 0.62 * r)} ${r2(cx + 0.02 * r)} ${r2(cy + 0.72 * r)}`;
  return { d, highlight, strokeWidth: r2(0.26 * r) };
})();

// Speed lines to the inline-start side of the word (left, as in the reference; the mark never mirrors).
const lineRight = r2(ar.box.minX - 7);
const speedLines = [
  { x: r2(lineRight - 40), y: -44, w: 40 },
  { x: r2(lineRight - 31), y: -31, w: 31 },
  { x: r2(lineRight - 23), y: -18, w: 23 },
].map((line) => ({ ...line, h: 6.2 }));

// ---- Latin "ZAWED" under the Arabic, matched to its width, with an italic lean.
const la = raw.la;
const latinScale = r2((ar.box.w * 0.96) / la.box.w);
const latinTop = r2(ar.box.maxY + 13);
const latinBaseline = r2(latinTop + la.box.h * latinScale);
const latinX = r2((ar.box.minX + ar.box.maxX) / 2 - ((la.box.minX + la.box.maxX) / 2) * latinScale + 4);
const latinTransform = `translate(${latinX} ${latinBaseline}) scale(${latinScale}) skewX(-11)`;
// Orange treatment: the last stroke of the W (right of a line parallel to that stroke).
const w = la.wBox;
const wClip = [
  [r2(w.minX + 0.705 * w.w), r2(w.minY - 6)],
  [r2(w.maxX + 6), r2(w.minY - 6)],
  [r2(w.maxX + 6), r2(w.maxY + 6)],
  [r2(w.minX + 0.6 * w.w), r2(w.maxY + 6)],
];

const pad = 4;
const fullView = {
  x: r2(speedLines[0].x - pad),
  y: r2(ar.box.minY - pad),
  w: 0,
  h: 0,
};
fullView.w = r2(ar.box.maxX + pad - fullView.x);
fullView.h = r2(latinBaseline + pad + 2 - fullView.y);
const compactView = { x: fullView.x, y: fullView.y, w: fullView.w, h: r2(ar.box.maxY + pad - fullView.y) };

// ---- App icon: W with the shadda above, white on the primary green, orange W stroke.
const icon = raw.icon;
const iconWScale = r2(36 / icon.wBox.w);
const iconWh = icon.wBox.h * iconWScale;
const iconShaddaScale = r2(17 / icon.shaddaBox.w);
const iconShaddaH = icon.shaddaBox.h * iconShaddaScale;
const gap = 3.2;
const stackH = iconShaddaH + gap + iconWh;
const stackTop = (64 - stackH) / 2;
const iconShaddaTransform = `translate(${r2(32 - ((icon.shaddaBox.minX + icon.shaddaBox.maxX) / 2) * iconShaddaScale + 3)} ${r2(stackTop - icon.shaddaBox.minY * iconShaddaScale)}) scale(${iconShaddaScale})`;
const iconWTransform = `translate(${r2(32 - ((icon.wBox.minX + icon.wBox.maxX) / 2) * iconWScale + 2.5)} ${r2(stackTop + iconShaddaH + gap + iconWh)}) scale(${iconWScale}) skewX(-11)`;
const iw = icon.wBox;
const iconWClip = [
  [r2(iw.minX + 0.705 * iw.w), r2(iw.minY - 6)],
  [r2(iw.maxX + 6), r2(iw.minY - 6)],
  [r2(iw.maxX + 6), r2(iw.maxY + 6)],
  [r2(iw.minX + 0.6 * iw.w), r2(iw.maxY + 6)],
];

const geometry = {
  fullView,
  compactView,
  arabicPath: ar.d,
  counterPath: ar.counterD,
  dropPath: drop.d,
  dropHighlight: drop.highlight,
  dropHighlightWidth: drop.strokeWidth,
  speedLines,
  latinTransform,
  latinPath: la.d,
  wPath: la.wD,
  wClip: wClip.map((p) => p.join(',')).join(' '),
  icon: {
    shaddaPath: icon.shaddaD,
    shaddaTransform: iconShaddaTransform,
    wPath: icon.wD,
    wTransform: iconWTransform,
    wClip: iconWClip.map((p) => p.join(',')).join(' '),
  },
};

// ---- Static SVG builders (concrete token colors)
const view = (v) => `${v.x} ${v.y} ${v.w} ${v.h}`;
function wordmarkSvg({ mark, accent }, { compact = false, title = 'زوّد ZAWED' } = {}) {
  const v = compact ? compactView : fullView;
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${view(v)}" role="img" aria-label="${title}">
  <title>${title}</title>
  <defs>
    <mask id="drop-cut" maskUnits="userSpaceOnUse" x="${v.x}" y="${v.y}" width="${v.w}" height="${v.h}">
      <rect x="${v.x}" y="${v.y}" width="${v.w}" height="${v.h}" fill="#fff"/>
      <path d="${drop.d}" fill="#000"/>
    </mask>
    <clipPath id="w-accent"><polygon points="${geometry.wClip}"/></clipPath>
  </defs>
  ${speedLines.map((l) => `<rect x="${l.x}" y="${l.y}" width="${l.w}" height="${l.h}" rx="${l.h / 2}" fill="${accent}"/>`).join('\n  ')}
  <g fill="${mark}" mask="url(#drop-cut)">
    <path d="${ar.d}"/>
    <path d="${ar.counterD}" stroke="${mark}" stroke-width="1.4" stroke-linejoin="round"/>
  </g>
  <path d="${drop.highlight}" fill="none" stroke="${accent}" stroke-width="${drop.strokeWidth}" stroke-linecap="round"/>
  ${compact ? '' : `<g transform="${latinTransform}">
    <path d="${la.d}" fill="${mark}"/>
    <path d="${la.wD}" fill="${mark}"/>
    <path d="${la.wD}" fill="${accent}" clip-path="url(#w-accent)"/>
  </g>`}
</svg>
`;
}

function iconSvg({ background = '#0F3B2C', mark = '#FFFFFF', accent = '#F28C28' } = {}) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="ZAWED">
  <title>ZAWED</title>
  <defs><clipPath id="icon-w-accent"><polygon points="${geometry.icon.wClip}"/></clipPath></defs>
  <rect width="64" height="64" rx="14" fill="${background}"/>
  <path d="${icon.shaddaD}" transform="${iconShaddaTransform}" fill="${mark}"/>
  <g transform="${iconWTransform}">
    <path d="${icon.wD}" fill="${mark}"/>
    <path d="${icon.wD}" fill="${accent}" clip-path="url(#icon-w-accent)"/>
  </g>
</svg>
`;
}

const outputs = {
  'zawed-logo-light.svg': wordmarkSvg(TOKENS.light),
  'zawed-logo-dark.svg': wordmarkSvg(TOKENS.dark),
  'zawed-logo-inverse.svg': wordmarkSvg(TOKENS.inverse),
  'zawed-logo-compact-light.svg': wordmarkSvg(TOKENS.light, { compact: true, title: 'زوّد' }),
  'zawed-logo-compact-dark.svg': wordmarkSvg(TOKENS.dark, { compact: true, title: 'زوّد' }),
  'zawed-app-icon.svg': iconSvg(),
};

const brandDir = path.join(repo, 'docs/brand');
fs.mkdirSync(brandDir, { recursive: true });
for (const [name, svg] of Object.entries(outputs)) fs.writeFileSync(path.join(brandDir, name), svg);
fs.writeFileSync(path.join(web, 'public/favicon.svg'), outputs['zawed-app-icon.svg']);

const tsDir = path.join(web, 'src/design-system/brand');
fs.mkdirSync(tsDir, { recursive: true });
fs.writeFileSync(
  path.join(tsDir, 'logo-geometry.ts'),
  `/**
 * ZAWED / زوّد logo geometry. GENERATED from the app's own fonts (IBM Plex Sans Arabic Bold for
 * "زوّد", IBM Plex Sans Bold for "ZAWED") shaped with HarfBuzz, composed after the brand reference:
 * speed lines, a fuel drop cut into the و, the shadda kept above the و, and ZAWED with an accent W stroke.
 * Colors are NOT part of the geometry: the component applies the existing theme tokens.
 * Regenerate rather than hand-edit (see docs/brand/README.md).
 */
export const LOGO = ${JSON.stringify(geometry, null, 2)} as const;
`,
);

const card = (bg, file) => `<div style="background:${bg};padding:24px;display:inline-block;margin:8px"><img src="file:///${path.join(brandDir, file).replace(/\\/g, '/')}" height="${file.includes('icon') ? 96 : 160}"></div>`;
fs.writeFileSync(
  'preview.html',
  `<html><body style="margin:0;background:#ddd">
${card('#F7F5F0', 'zawed-logo-light.svg')}${card('#0C1411', 'zawed-logo-dark.svg')}${card('#0F3B2C', 'zawed-logo-inverse.svg')}<br>
${card('#FFFFFF', 'zawed-logo-compact-light.svg')}${card('#0C1411', 'zawed-logo-compact-dark.svg')}${card('#F7F5F0', 'zawed-app-icon.svg')}
</body></html>`,
);
console.log('written', Object.keys(outputs).length, 'svgs', JSON.stringify(fullView), JSON.stringify(compactView));
