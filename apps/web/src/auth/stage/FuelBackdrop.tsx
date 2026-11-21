
import './backdrop.css';
import { useEffect, useRef, type CSSProperties } from 'react';
import { DROP_PATH, SPEED_LINE_WIDTHS } from './marks';

/**
 * The sign-in backdrop: ZAWED's own marks in slow motion. Groups of the logo's three speed lines
 * stream across in the reading direction like fuel through a line, a few drops rise and fade, a
 * large soft drop breathes behind the card, and two fuel waves roll along the bottom edge.
 *
 * CSS transforms and opacity only (backdrop.css), mounted after the card paints. It pauses while the tab is
 * hidden and holds a still composition under reduced motion.
 */

/** Each stream: vertical position (%), length (px), duration (s), start offset (0–1), colour, thickness. */
const STREAMS: Array<{ y: number; length: number; duration: number; offset: number; tone: 'accent' | 'primary'; thick: number; rest: number }> = [
  { y: 12, length: 220, duration: 19, offset: 0.1, tone: 'accent', thick: 5, rest: 8 },
  { y: 22, length: 140, duration: 24, offset: 0.55, tone: 'primary', thick: 4, rest: 64 },
  { y: 34, length: 260, duration: 21, offset: 0.3, tone: 'primary', thick: 6, rest: 18 },
  { y: 47, length: 160, duration: 17, offset: 0.8, tone: 'accent', thick: 4, rest: 78 },
  { y: 58, length: 240, duration: 26, offset: 0.05, tone: 'primary', thick: 5, rest: 4 },
  { y: 69, length: 180, duration: 20, offset: 0.65, tone: 'accent', thick: 5, rest: 70 },
  { y: 80, length: 120, duration: 23, offset: 0.4, tone: 'primary', thick: 4, rest: 30 },
  { y: 88, length: 200, duration: 18, offset: 0.9, tone: 'accent', thick: 4, rest: 84 },
];

/** Each droplet: horizontal position (%), size (px), duration (s), start offset (0–1). */
const DROPLETS = [
  { x: 8, size: 14, duration: 16, offset: 0.2 },
  { x: 18, size: 9, duration: 21, offset: 0.7 },
  { x: 31, size: 12, duration: 18, offset: 0.45 },
  { x: 68, size: 10, duration: 20, offset: 0.1 },
  { x: 79, size: 15, duration: 17, offset: 0.6 },
  { x: 91, size: 8, duration: 23, offset: 0.35 },
];

/** The logo's speed-line lengths, relative to the longest. */
const LINE_RATIOS = SPEED_LINE_WIDTHS.map((width) => width / SPEED_LINE_WIDTHS[0]);
const DROP_VIEW = '80.5 -38.7 19.2 25.4';
const WAVE = 'M0 20 q50 -14 100 0 t100 0 t100 0 t100 0 t100 0 t100 0 t100 0 t100 0 V60 H0 Z';

export default function FuelBackdrop() {
  const root = useRef<HTMLDivElement>(null);

  // Nothing moves while the tab is in the background.
  useEffect(() => {
    const element = root.current;
    if (!element) return;
    const sync = () => element.toggleAttribute('data-paused', document.visibilityState === 'hidden');
    sync();
    document.addEventListener('visibilitychange', sync);
    return () => document.removeEventListener('visibilitychange', sync);
  }, []);

  return (
    <div ref={root} aria-hidden className="fuel-backdrop">
      <div className="fuel-backdrop-glow" />
      <svg className="fuel-backdrop-drop" viewBox={DROP_VIEW}>
        <defs>
          <radialGradient id="fuel-backdrop-drop-fill" cx="50%" cy="62%" r="60%">
            <stop offset="0%" className="fuel-backdrop-drop-core" />
            <stop offset="100%" className="fuel-backdrop-drop-edge" />
          </radialGradient>
        </defs>
        <path d={DROP_PATH} fill="url(#fuel-backdrop-drop-fill)" />
      </svg>

      <div className="fuel-streams">
        {STREAMS.map((stream) => (
          <div
            key={stream.y}
            className="fuel-stream"
            data-tone={stream.tone}
            style={
              {
                '--y': `${stream.y}%`,
                '--length': `${stream.length}px`,
                '--duration': `${stream.duration}s`,
                '--delay': `${-stream.offset * stream.duration}s`,
                '--thick': `${stream.thick}px`,
                '--rest': `${stream.rest}vw`,
              } as CSSProperties
            }
          >
            {LINE_RATIOS.map((ratio) => (
              <span key={ratio} style={{ width: `${ratio * 100}%` }} />
            ))}
          </div>
        ))}
      </div>

      <div className="fuel-droplets">
        {DROPLETS.map((drop) => (
          <svg
            key={drop.x}
            viewBox={DROP_VIEW}
            className="fuel-droplet"
            style={
              {
                '--x': `${drop.x}%`,
                '--size': `${drop.size}px`,
                '--duration': `${drop.duration}s`,
                '--delay': `${-drop.offset * drop.duration}s`,
              } as CSSProperties
            }
          >
            <path d={DROP_PATH} />
          </svg>
        ))}
      </div>

      <div className="fuel-waves">
        <svg viewBox="0 0 800 60" preserveAspectRatio="none" className="fuel-wave fuel-wave-back">
          <path d={WAVE} />
        </svg>
        <svg viewBox="0 0 800 60" preserveAspectRatio="none" className="fuel-wave fuel-wave-front">
          <path d={WAVE} />
        </svg>
      </div>
    </div>
  );
}
