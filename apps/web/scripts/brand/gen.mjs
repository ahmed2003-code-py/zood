// Generates ZAWED logo geometry from IBM Plex Sans Arabic / IBM Plex Sans Bold (the app's own fonts).
// Usage: node gen.mjs <apps/web dir>
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import * as hb from 'harfbuzzjs';

const web = process.argv[2];
const fontsArabic = path.join(web, 'node_modules/@fontsource/ibm-plex-sans-arabic/files/ibm-plex-sans-arabic-arabic-700-normal.woff');
const fontsLatin = path.join(web, 'node_modules/@fontsource/ibm-plex-sans/files/ibm-plex-sans-latin-700-normal.woff');

/** WOFF 1.0 → SFNT (tables are zlib-compressed individually). */
function woffToSfnt(buffer) {
  const flavor = buffer.readUInt32BE(4);
  const numTables = buffer.readUInt16BE(12);
  const tables = [];
  for (let i = 0; i < numTables; i++) {
    const base = 44 + i * 20;
    const tag = buffer.readUInt32BE(base);
    const offset = buffer.readUInt32BE(base + 4);
    const compLength = buffer.readUInt32BE(base + 8);
    const origLength = buffer.readUInt32BE(base + 12);
    const checksum = buffer.readUInt32BE(base + 16);
    const raw = buffer.subarray(offset, offset + compLength);
    const data = compLength < origLength ? zlib.inflateSync(raw) : raw;
    tables.push({ tag, checksum, data });
  }
  let entrySelector = 0;
  while (1 << (entrySelector + 1) <= numTables) entrySelector++;
  const searchRange = (1 << entrySelector) * 16;
  const headerSize = 12 + numTables * 16;
  let offset = headerSize;
  const placed = tables.map((table) => {
    const at = offset;
    offset += (table.data.length + 3) & ~3;
    return { ...table, at };
  });
  const out = Buffer.alloc(offset);
  out.writeUInt32BE(flavor, 0);
  out.writeUInt16BE(numTables, 4);
  out.writeUInt16BE(searchRange, 6);
  out.writeUInt16BE(entrySelector, 8);
  out.writeUInt16BE(numTables * 16 - searchRange, 10);
  placed.forEach((table, i) => {
    const base = 12 + i * 16;
    out.writeUInt32BE(table.tag, base);
    out.writeUInt32BE(table.checksum, base + 4);
    out.writeUInt32BE(table.at, base + 8);
    out.writeUInt32BE(table.data.length, base + 12);
    table.data.copy(out, table.at);
  });
  return out;
}

function loadFont(file) {
  const sfnt = woffToSfnt(fs.readFileSync(file));
  const face = new hb.Face(new hb.Blob(sfnt.buffer.slice(sfnt.byteOffset, sfnt.byteOffset + sfnt.byteLength)));
  return { face, font: new hb.Font(face), upem: face.upem };
}

/** Parses an hb SVG path (font units, y up) into subpaths of absolute commands. */
function parsePath(d) {
  const tokens = d.match(/[MLQCZ]|-?\d*\.?\d+(?:e-?\d+)?/g) ?? [];
  const subpaths = [];
  let current = null;
  let i = 0;
  while (i < tokens.length) {
    const cmd = tokens[i++];
    const take = (n) => tokens.slice(i, (i += n)).map(Number);
    if (cmd === 'M') {
      current = [{ cmd, pts: take(2) }];
      subpaths.push(current);
    } else if (cmd === 'L') current.push({ cmd, pts: take(2) });
    else if (cmd === 'Q') current.push({ cmd, pts: take(4) });
    else if (cmd === 'C') current.push({ cmd, pts: take(6) });
    else if (cmd === 'Z') current.push({ cmd, pts: [] });
  }
  return subpaths;
}

const round = (value) => Math.round(value * 100) / 100;

/** Shapes text and returns positioned glyph subpaths in a y-down coordinate space (scale = px per unit). */
function shapeText({ font, upem }, text, scale, originX = 0, baselineY = 0) {
  const buffer = new hb.Buffer();
  buffer.addText(text);
  buffer.guessSegmentProperties();
  hb.shape(font, buffer);
  const infos = buffer.getGlyphInfos();
  const positions = buffer.getGlyphPositions();
  let pen = 0;
  const glyphs = infos.map((info, index) => {
    const position = positions[index];
    const x0 = pen + position.xOffset;
    const y0 = position.yOffset;
    pen += position.xAdvance;
    const subpaths = parsePath(font.glyphToPath(info.codepoint)).map((sub) =>
      sub.map(({ cmd, pts }) => ({
        cmd,
        pts: pts.map((value, k) => (k % 2 === 0 ? round(originX + (value + x0) * scale) : round(baselineY - (value + y0) * scale))),
      })),
    );
    return { cluster: info.cluster, subpaths };
  });
  buffer.destroy?.();
  return { glyphs, advance: pen * scale, upem };
}

const toD = (subpaths) => subpaths.map((sub) => sub.map(({ cmd, pts }) => cmd + pts.join(' ')).join('')).join('');

function bbox(subpaths) {
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const sub of subpaths) for (const { pts } of sub) for (let k = 0; k < pts.length; k += 2) {
    minX = Math.min(minX, pts[k]); maxX = Math.max(maxX, pts[k]);
    minY = Math.min(minY, pts[k + 1]); maxY = Math.max(maxY, pts[k + 1]);
  }
  return { minX, minY, maxX, maxY, w: maxX - minX, h: maxY - minY };
}

const hbReady = hb.default ?? hb;
void hbReady;

const arabic = loadFont(fontsArabic);
const latin = loadFont(fontsLatin);

// --- Arabic wordmark: زوّد (ز + و + shadda + د). Only the shadda as a diacritic.
const WORD = 'زوّد';
const AR_SCALE = 0.12; // 1000 upem → 120 px em
const ar = shapeText(arabic, WORD, AR_SCALE, 0, 0);
const arAll = ar.glyphs.flatMap((glyph) => glyph.subpaths);
const arBox = bbox(arAll);

// The و is cluster 1 (its shadda shares the cluster). Its counter is the subpath nested inside another.
const waw = ar.glyphs.filter((glyph) => glyph.cluster === 1);
const wawBody = waw.reduce((best, glyph) => (bbox(glyph.subpaths).h > bbox(best.subpaths).h ? glyph : best));
const wawSubs = wawBody.subpaths.map((sub) => ({ sub, box: bbox([sub]) }));
const outer = wawSubs.reduce((a, b) => (a.box.w * a.box.h > b.box.w * b.box.h ? a : b));
const counter = wawSubs.filter((s) => s !== outer).reduce((a, b) => (a && a.box.w * a.box.h > b.box.w * b.box.h ? a : b), null);

console.log(JSON.stringify({ glyphs: ar.glyphs.map((g) => ({ cluster: g.cluster, subs: g.subpaths.length, box: bbox(g.subpaths) })), arBox, counter: counter?.box, outer: outer.box }, null, 1));

// --- Latin: ZAWED
const LAT_SCALE = 0.046;
const la = shapeText(latin, 'ZAWED', LAT_SCALE, 0, 0);
const laAll = la.glyphs.flatMap((glyph) => glyph.subpaths);
const laBox = bbox(laAll);
const wGlyph = la.glyphs[2];
console.log(JSON.stringify({ laBox, w: bbox(wGlyph.subpaths), advance: la.advance }));

// --- Icon: W + shadda
const ICON_W_SCALE = 0.056;
const iconW = shapeText(latin, 'W', ICON_W_SCALE, 0, 0);
const shadda = shapeText(arabic, 'ّ', 0.12, 0, 0); // isolated mark glyph
console.log(JSON.stringify({ iconW: bbox(iconW.glyphs[0].subpaths), shadda: shadda.glyphs.map((g) => bbox(g.subpaths)) }));

fs.writeFileSync('raw.json', JSON.stringify({
  ar: { d: toD(arAll), box: arBox, counterD: counter ? toD([counter.sub]) : '', counterBox: counter?.box },
  la: { d: toD(la.glyphs.filter((_, i) => i !== 2).flatMap((g) => g.subpaths)), wD: toD(wGlyph.subpaths), box: laBox, wBox: bbox(wGlyph.subpaths) },
  icon: { wD: toD(iconW.glyphs[0].subpaths), wBox: bbox(iconW.glyphs[0].subpaths), shaddaD: toD(shadda.glyphs.flatMap((g) => g.subpaths)), shaddaBox: bbox(shadda.glyphs.flatMap((g) => g.subpaths)) },
}, null, 1));
console.log('ok');
