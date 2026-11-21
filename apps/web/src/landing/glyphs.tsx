import { useId, type ReactNode } from 'react';
import { cn } from '@/lib/cn';

/**
 * The landing page's own icon set (pass 3): duotone glyphs drawn on a 48 grid with one stroke
 * (2.5), round caps and joins, and 4-unit corners. Ink is the brand green, the soft fill is the
 * second tone, and one orange detail carries the idea (the drop that falls, the needle, the coin).
 * Colours come from the --glyph-* tokens (tokens.css), so every glyph follows Light and Dark.
 *
 * Each glyph has one small motion (classes m-*, landing.css): it plays once when its section is
 * revealed and again on hover, never loops on its own, and is off under reduced motion. The glyphs
 * are decorative (aria-hidden): the heading next to each one says what it means.
 *
 * Class names inside the SVG: f = soft fill, a = accent stroke, af = accent fill, bg = knockout in
 * the tile colour (so a badge can sit over a shape), k = stroke in the tile colour (a check on an
 * accent badge).
 */

export type GlyphName =
  | 'tag'
  | 'wallet'
  | 'pump'
  | 'drop'
  | 'gauge'
  | 'timer'
  | 'bell'
  | 'departments'
  | 'statement'
  | 'approval'
  | 'ledger'
  | 'sync'
  | 'egypt'
  | 'bank'
  | 'cash'
  | 'coins'
  | 'shield'
  | 'clock'
  | 'language'
  | 'support'
  | 'pos'
  | 'shifts'
  | 'tagOff'
  | 'leak'
  | 'paper'
  | 'cards'
  | 'payable';

/** The drop outline, shared by the litre, leak and payable glyphs. */
const DROP = 'M24 6.5c-5.6 7-11 13.6-11 20.5a11 11 0 0 0 22 0c0-6.9-5.4-13.5-11-20.5z';

function Drop({ clip }: { clip: string }) {
  return (
    <>
      <clipPath id={clip}>
        <path d={DROP} />
      </clipPath>
      <g clipPath={`url(#${clip})`}>
        <g className="m-level">
          <path className="f" d="M8 29c4-2.4 8-2.4 12 0s8 2.4 12 0 8-2.4 12 0v20H8z" />
          <path className="a" d="M8 29c4-2.4 8-2.4 12 0s8 2.4 12 0 8-2.4 12 0" />
        </g>
      </g>
      <path d={DROP} />
    </>
  );
}

function shapes(name: GlyphName, clip: string): ReactNode {
  switch (name) {
    case 'tag':
      return (
        <>
          <rect className="f" x="5" y="13" width="26" height="22" rx="4" />
          <rect x="5" y="13" width="26" height="22" rx="4" />
          <rect x="10" y="18.5" width="7" height="6" rx="1.5" />
          <path d="M10 29.5h11" />
          <path className="a m-wave m-wave-1" d="M35.5 19.5a6.5 6.5 0 0 1 0 9" />
          <path className="a m-wave m-wave-2" d="M40 15.5a12.5 12.5 0 0 1 0 17" />
        </>
      );
    case 'cards':
      return (
        <>
          <rect x="13" y="9" width="28" height="19" rx="4" transform="rotate(-10 27 18.5)" />
          <g className="m-slide">
            <rect className="bg" x="7" y="19" width="28" height="19" rx="4" />
            <rect className="f" x="7" y="19" width="28" height="19" rx="4" />
            <rect x="7" y="19" width="28" height="19" rx="4" />
            <path d="M7 25.5h28" />
            <path className="a" d="M12 32.5h7" />
          </g>
        </>
      );
    case 'tagOff':
      return (
        <>
          <rect className="f" x="5" y="10" width="28" height="22" rx="4" />
          <rect x="5" y="10" width="28" height="22" rx="4" />
          <rect x="10" y="15.5" width="7" height="6" rx="1.5" />
          <path d="M10 26.5h10" />
          <circle className="bg" cx="34" cy="33" r="10" />
          <g className="m-pop">
            <circle className="a" cx="34" cy="33" r="7.5" />
            <path className="a" d="M28.8 38.2l10.4-10.4" />
          </g>
        </>
      );
    case 'wallet':
      return (
        <>
          <path className="f" d="M7 18h29a4 4 0 0 1 4 4v16a4 4 0 0 1-4 4H11a4 4 0 0 1-4-4z" />
          <path d="M7 18h29a4 4 0 0 1 4 4v16a4 4 0 0 1-4 4H11a4 4 0 0 1-4-4V16a4 4 0 0 1 4-4h15" />
          <path className="bg" d="M40 26h-6a4 4 0 0 0 0 8h6z" />
          <path d="M40 26h-6a4 4 0 0 0 0 8h6" />
          <path className="a" d="M34 30h.01" />
          <g className="m-drop-in">
            <circle className="af" cx="35" cy="9.5" r="6" />
            <path className="k" d="M35 6.8v5.4M32.3 9.5h5.4" />
          </g>
        </>
      );
    case 'pump':
      return (
        <>
          <path className="f" d="M9 12a4 4 0 0 1 4-4h12a4 4 0 0 1 4 4v29H9z" />
          <path d="M9 41V12a4 4 0 0 1 4-4h12a4 4 0 0 1 4 4v29M5.5 41h27" />
          <rect className="bg" x="13" y="12.5" width="12" height="8" rx="2" />
          <rect x="13" y="12.5" width="12" height="8" rx="2" />
          <path d="M29 19h3.5a2.5 2.5 0 0 1 2.5 2.5V33a2.5 2.5 0 0 0 5 0V18.5L36 14" />
          <path className="af m-fall" d="M19 25.5l-2.8 4a3.4 3.4 0 1 0 5.6 0z" />
        </>
      );
    case 'drop':
      return <Drop clip={clip} />;
    case 'leak':
      return (
        <>
          <path className="f" d="M24 4.5c-5.6 7-11 13.6-11 20.5a11 11 0 0 0 22 0c0-6.9-5.4-13.5-11-20.5z" />
          <path d="M24 4.5c-5.6 7-11 13.6-11 20.5a11 11 0 0 0 22 0c0-6.9-5.4-13.5-11-20.5z" />
          <path d="M19.5 25.5a5 5 0 0 0 4.5 4.5" />
          <path className="af m-fall" d="M33 37.5l-2 2.9a2.5 2.5 0 1 0 4 0z" />
          <path className="af m-fall m-fall-2" d="M15 39.5l-1.6 2.3a2 2 0 1 0 3.2 0z" />
        </>
      );
    case 'payable':
      return (
        <>
          <Drop clip={clip} />
          <circle className="bg" cx="36" cy="35" r="9" />
          <g className="m-pop">
            <circle className="af" cx="36" cy="35" r="6.5" />
            <path className="k" d="M33.2 35.2l2 2 3.6-3.8" />
          </g>
        </>
      );
    case 'gauge':
      return (
        <>
          <path className="f" d="M7 33a17 17 0 0 1 34 0z" />
          <path d="M7 33a17 17 0 0 1 34 0" />
          <path className="a" d="M36 21a17 17 0 0 1 5 12" />
          <path d="M12 23.5l2.5 1.6M24 16v3" />
          <path d="M6 38.5h36" />
          <g className="m-needle">
            <path d="M24 33l7.5-9.5" />
          </g>
          <circle className="bg" cx="24" cy="33" r="3.2" />
          <circle cx="24" cy="33" r="3.2" />
        </>
      );
    case 'timer':
      return (
        <>
          <circle className="f" cx="24" cy="27" r="15" />
          <circle cx="24" cy="27" r="15" />
          <path d="M19.5 6.5h9M24 6.5V12M36 14.5l2.5-2.5" />
          <path className="a m-sweep" pathLength="1" d="M24 16a11 11 0 0 1 11 11" />
          <g className="m-hand">
            <path d="M24 27v-7" />
          </g>
          <circle className="af" cx="24" cy="27" r="2.2" />
        </>
      );
    case 'clock':
      return (
        <>
          <circle className="f" cx="24" cy="24" r="17" />
          <circle cx="24" cy="24" r="17" />
          <path d="M24 10.5v2.5M24 35v2.5M10.5 24H13M35 24h2.5" />
          <path d="M24 24v-8" />
          <g className="m-hand">
            <path className="a" d="M24 24l6.5 4" />
          </g>
          <circle cx="24" cy="24" r="1.4" />
        </>
      );
    case 'bell':
      return (
        <>
          <g className="m-ring">
            <path className="f" d="M14 31V22a10 10 0 0 1 20 0v9l3 4.5H11z" />
            <path d="M14 31V22a10 10 0 0 1 20 0v9l3 4.5H11z" />
            <path d="M20.5 40a3.5 3.5 0 0 0 7 0" />
            <path d="M24 8v4" />
          </g>
          <circle className="bg" cx="35" cy="12" r="7" />
          <circle className="af m-pop" cx="35" cy="12" r="4.8" />
        </>
      );
    case 'departments':
      return (
        <>
          <rect className="f" x="16.5" y="5" width="15" height="11" rx="3" />
          <rect x="16.5" y="5" width="15" height="11" rx="3" />
          <path className="m-draw" pathLength="1" d="M24 16v6M11 28v-6h26v6M24 22v6" />
          <rect x="5" y="28" width="12" height="11" rx="3" />
          <rect className="f" x="18" y="28" width="12" height="11" rx="3" />
          <rect className="a" x="18" y="28" width="12" height="11" rx="3" />
          <rect x="31" y="28" width="12" height="11" rx="3" />
        </>
      );
    case 'statement':
      return (
        <>
          <path className="f" d="M11 6h26v36l-4.3-3-4.4 3-4.3-3-4.3 3-4.4-3-4.3 3z" />
          <path d="M11 6h26v36l-4.3-3-4.4 3-4.3-3-4.3 3-4.4-3-4.3 3z" />
          <path className="m-draw" pathLength="1" d="M17 14h14" />
          <path className="m-draw m-draw-2" pathLength="1" d="M17 20h14" />
          <path className="m-draw m-draw-3" pathLength="1" d="M17 26h8" />
          <path className="a m-draw m-draw-4" pathLength="1" d="M17 32.5h14" />
        </>
      );
    case 'paper':
      return (
        <>
          <path d="M15 6.5h15" />
          <path className="f" d="M10 11h18l8 8v23H10z" />
          <path d="M28 11H10v31h26V19zM28 11v8h8" />
          <path d="M15 24h10M15 29h15" />
          <path className="a m-draw" pathLength="1" d="M15 36c1.6-2.4 3.2 2.4 4.8 0s3.2 2.4 4.8 0 3.2 2.4 4.8 0" />
        </>
      );
    case 'approval':
      return (
        <>
          <circle className="f" cx="17" cy="15" r="5.5" />
          <circle cx="17" cy="15" r="5.5" />
          <path d="M6 36a11 11 0 0 1 22 0" />
          <circle cx="31" cy="15" r="5.5" />
          <path d="M26.5 25.8A11 11 0 0 1 35 25" />
          <circle className="bg" cx="36" cy="35" r="9" />
          <g className="m-pop">
            <circle className="af" cx="36" cy="35" r="6.8" />
            <path className="k" d="M33 35.2l2.1 2.1 3.9-4" />
          </g>
        </>
      );
    case 'ledger':
      return (
        <>
          <path d="M24 9v30M16 40h16" />
          <g className="m-balance">
            <path d="M9 14h30" />
            <path d="M9 14l-4.5 11M9 14l4.5 11M39 14l-4.5 11M39 14l4.5 11" />
            <path className="f" d="M3.5 25h11a5.5 5.5 0 0 1-11 0zM33.5 25h11a5.5 5.5 0 0 1-11 0z" />
            <path d="M3.5 25h11a5.5 5.5 0 0 1-11 0zM33.5 25h11a5.5 5.5 0 0 1-11 0z" />
          </g>
          <circle className="af" cx="24" cy="10" r="3" />
        </>
      );
    case 'sync':
      return (
        <>
          <rect className="f" x="9" y="5" width="20" height="34" rx="4" />
          <rect x="9" y="5" width="20" height="34" rx="4" />
          <path d="M16.5 10h5M16.5 33.5h5" />
          <circle className="bg" cx="34" cy="33" r="11" />
          <g className="m-spin">
            <path className="a" d="M27.3 31a7 7 0 0 1 12.3-2.7M40.7 35a7 7 0 0 1-12.3 2.7" />
            <path className="a" d="M40 24.5v4h-4M28 41.5v-4h4" />
          </g>
        </>
      );
    case 'egypt':
      return (
        <>
          <circle className="af m-rise" cx="36" cy="12" r="5" />
          <path className="f" d="M4.5 40L19 15l14.5 25z" />
          <path d="M4.5 40L19 15l14.5 25M19 15l3.5 25" />
          <path d="M28.5 31.5L34 23l9.5 17" />
          <path d="M3 40h42" />
        </>
      );
    case 'bank':
      return (
        <>
          <path className="f" d="M6 18L24 7l18 11z" />
          <path d="M6 18L24 7l18 11zM10 22v12M19 22v12M29 22v12M38 22v12M6 38.5h36" />
          <circle className="af m-pop" cx="24" cy="13.5" r="2.4" />
        </>
      );
    case 'cash':
      return (
        <>
          <g className="m-slide">
            <rect className="f" x="4" y="13" width="40" height="22" rx="4" />
            <rect x="4" y="13" width="40" height="22" rx="4" />
            <circle className="a" cx="24" cy="24" r="5" />
            <path d="M10 19.5v.01M38 28.5v.01" />
          </g>
        </>
      );
    case 'coins':
      return (
        <>
          <path className="f" d="M6 23v11c0 2.8 5.8 5 13 5s13-2.2 13-5V23" />
          <ellipse className="bg" cx="19" cy="23" rx="13" ry="5" />
          <path d="M6 23v11c0 2.8 5.8 5 13 5s13-2.2 13-5V23M6 28.5c0 2.8 5.8 5 13 5s13-2.2 13-5" />
          <ellipse cx="19" cy="23" rx="13" ry="5" />
          <g className="m-drop-in">
            <circle className="af" cx="36" cy="11" r="6.5" />
            <path className="k" d="M36 8v6" />
          </g>
        </>
      );
    case 'shield':
      return (
        <>
          <path className="f" d="M24 5l15 6v11c0 9.5-6.3 16.5-15 21-8.7-4.5-15-11.5-15-21V11z" />
          <path d="M24 5l15 6v11c0 9.5-6.3 16.5-15 21-8.7-4.5-15-11.5-15-21V11z" />
          <g className="m-pop">
            <rect className="af" x="18" y="21" width="12" height="10" rx="2.5" />
            <path className="a" d="M20.5 21v-2.5a3.5 3.5 0 0 1 7 0V21" />
          </g>
        </>
      );
    case 'language':
      return (
        <>
          <path className="f" d="M5 10a4 4 0 0 1 4-4h17a4 4 0 0 1 4 4v10a4 4 0 0 1-4 4H16l-6 5v-5H9a4 4 0 0 1-4-4z" />
          <path d="M5 10a4 4 0 0 1 4-4h17a4 4 0 0 1 4 4v10a4 4 0 0 1-4 4H16l-6 5v-5H9a4 4 0 0 1-4-4z" />
          <path d="M11 12.5h13M11 17.5h8" />
          <path className="bg" d="M22 23a4 4 0 0 1 4-4h13a4 4 0 0 1 4 4v9a4 4 0 0 1-4 4h-1v5l-6-5h-6a4 4 0 0 1-4-4z" />
          <g className="m-pop">
            <path className="a" d="M22 23a4 4 0 0 1 4-4h13a4 4 0 0 1 4 4v9a4 4 0 0 1-4 4h-1v5l-6-5h-6a4 4 0 0 1-4-4z" />
            <path className="a" d="M28 25.5h9M28 30h5" />
          </g>
        </>
      );
    case 'support':
      return (
        <>
          <path d="M10 27v-4a14 14 0 0 1 28 0v4" />
          <rect className="f" x="6" y="25" width="8" height="12" rx="3" />
          <rect x="6" y="25" width="8" height="12" rx="3" />
          <rect className="f" x="34" y="25" width="8" height="12" rx="3" />
          <rect x="34" y="25" width="8" height="12" rx="3" />
          <path className="a m-draw" pathLength="1" d="M38 37c0 3.5-3 5.5-8 5.5h-3" />
          <circle className="af" cx="25" cy="42.5" r="2.2" />
        </>
      );
    case 'pos':
      return (
        <>
          <rect className="f" x="12" y="5" width="24" height="38" rx="4" />
          <rect x="12" y="5" width="24" height="38" rx="4" />
          <rect className="bg" x="16.5" y="10" width="15" height="12" rx="2" />
          <rect x="16.5" y="10" width="15" height="12" rx="2" />
          <path className="a m-draw" pathLength="1" d="M20.5 16l2.5 2.5 4.5-4.5" />
          <path d="M19 28.5h.01M24 28.5h.01M29 28.5h.01M19 33.5h.01M24 33.5h.01M29 33.5h.01M19 38.5h.01M24 38.5h.01M29 38.5h.01" />
        </>
      );
    case 'shifts':
      return (
        <>
          <rect className="f" x="5" y="9" width="30" height="29" rx="4" />
          <rect x="5" y="9" width="30" height="29" rx="4" />
          <path d="M5 17h30M13 5.5v7M27 5.5v7M11 24h4M18 24h4M11 30h4" />
          <circle className="bg" cx="35" cy="34" r="10" />
          <circle className="a" cx="35" cy="34" r="7.5" />
          <g className="m-hand m-hand-at">
            <path className="a" d="M35 34v-4" />
          </g>
          <path className="a" d="M35 34l2.8 1.6" />
        </>
      );
  }
}

export type GlyphSize = 'sm' | 'md' | 'lg' | 'bare';

/** A glyph in its soft tile (sm 44, md 56, lg 72 px), or bare for small inline marks. */
export function Glyph({ name, size = 'md', className }: { name: GlyphName; size?: GlyphSize; className?: string }) {
  const clip = `glyph-${useId().replace(/:/g, '')}`;
  const svg = (
    <svg viewBox="0 0 48 48" aria-hidden="true" focusable="false" className="landing-glyph-svg">
      {shapes(name, clip)}
    </svg>
  );
  if (size === 'bare') return <span className={cn('landing-glyph landing-glyph-bare', className)}>{svg}</span>;
  return (
    <span data-size={size} className={cn('landing-glyph landing-glyph-tile', className)}>
      {svg}
    </span>
  );
}
