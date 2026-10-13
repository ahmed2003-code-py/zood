import { describe, expect, it } from 'vitest';
import tokensSource from './tokens.css?raw';

const tokensCss = tokensSource.replace(/\/\*[\s\S]*?\*\//g, '');

/** Collects custom properties declared in every rule whose selector contains `selectorPart`. */
function declarations(selectorPart: string): Record<string, string> {
  const result: Record<string, string> = {};
  const rule = /([^{}]+)\{([^{}]*)\}/g;
  for (const match of tokensCss.matchAll(rule)) {
    const selector = match[1] ?? '';
    if (!selector.split(',').some((part) => part.trim() === selectorPart)) continue;
    for (const declaration of (match[2] ?? '').matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)) {
      if (declaration[1] && declaration[2]) result[declaration[1]] = declaration[2].trim();
    }
  }
  return result;
}

const primitives = declarations(':root');

function resolve(theme: Record<string, string>, name: string): string {
  let value = theme[name] ?? primitives[name];
  for (let depth = 0; value?.startsWith('var(') && depth < 5; depth++) {
    const ref = value.slice(4, -1).trim();
    value = theme[ref] ?? primitives[ref];
  }
  if (!value || !/^#[0-9a-f]{6}$/i.test(value)) throw new Error(`Token ${name} does not resolve to a hex color: ${value}`);
  return value;
}

function luminance(hex: string): number {
  const channels = [1, 3, 5].map((index) => parseInt(hex.slice(index, index + 2), 16) / 255);
  const [r = 0, g = 0, b = 0] = channels.map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a: string, b: string): number {
  const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (high + 0.05) / (low + 0.05);
}

const TEXT = 4.5;
const UI = 3;

const PAIRS: Array<[foreground: string, background: string, minimum: number]> = [
  ['--foreground', '--background', TEXT],
  ['--foreground', '--surface', TEXT],
  ['--foreground', '--surface-elevated', TEXT],
  ['--foreground', '--surface-sunken', TEXT],
  ['--muted', '--background', TEXT],
  ['--muted', '--surface', TEXT],
  ['--primary-foreground', '--primary', TEXT],
  ['--primary-subtle-foreground', '--primary-subtle', TEXT],
  ['--accent-foreground', '--accent', TEXT],
  ['--danger-foreground', '--danger', TEXT],
  ['--success', '--surface', TEXT],
  ['--success', '--success-subtle', TEXT],
  ['--warning', '--surface', TEXT],
  ['--warning', '--warning-subtle', TEXT],
  ['--danger', '--surface', TEXT],
  ['--danger', '--danger-subtle', TEXT],
  ['--info', '--surface', TEXT],
  ['--info', '--info-subtle', TEXT],
  ['--sidebar-foreground', '--sidebar', TEXT],
  ['--sidebar-active-foreground', '--sidebar-active', TEXT],
  ['--sidebar-foreground', '--hero', TEXT],
  ['--input-border', '--input', UI],
  ['--border-strong', '--surface', UI],
  ['--focus-ring', '--background', UI],
  ['--primary', '--background', UI],
  ['--chart-1', '--surface', UI],
  ['--chart-2', '--surface', UI],
  ['--chart-axis', '--surface', TEXT],
];

describe.each([
  ['light', { ...declarations(':root'), ...declarations("[data-theme='light']") }],
  ['dark', declarations("[data-theme='dark']")],
])('%s theme contrast', (_name, theme) => {
  it.each(PAIRS)('%s on %s', (foreground, background, minimum) => {
    const ratio = contrast(resolve(theme, foreground), resolve(theme, background));
    expect(ratio, `${foreground} on ${background} = ${ratio.toFixed(2)}`).toBeGreaterThanOrEqual(minimum);
  });
});

describe('POS dark theme', () => {
  const posDark = { ...declarations("[data-theme='dark']"), ...declarations("[data-surface='pos'][data-theme='dark']") };
  it.each([
    ['--foreground', '--background'],
    ['--foreground', '--surface'],
    ['--muted', '--surface'],
  ])('%s on %s is at least 7:1', (foreground, background) => {
    expect(contrast(resolve(posDark, foreground), resolve(posDark, background))).toBeGreaterThanOrEqual(7);
  });
});
