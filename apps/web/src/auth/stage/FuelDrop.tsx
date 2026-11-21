import { useId } from 'react';
import { cn } from '@/lib/cn';
import { DROP_HIGHLIGHT, DROP_PATH, SPEED_LINE_WIDTHS } from './marks';
import type { SignInState } from './state';

/** The logo's speed lines, half size, right-aligned against the drop like in the mark. */
const SPEED_LINES = SPEED_LINE_WIDTHS.map((width, index) => ({
  width: width / 2,
  y: -31.6 + index * 6.5,
}));
const LINE_END = 79.2;
const LINE_HEIGHT = 3.1;

/**
 * The sign-in emblem, made of the logo's own parts: its three speed lines and the fuel drop cut
 * into the و. The drop holds fuel whose level tells the state of the sign-in:
 * idle (a little fuel, a slow wave), working (filling), success (full, then a check), error
 * (drains, then settles back). Levels and motion live in signin.css; under reduced motion the
 * level still changes, without animation.
 */
export function FuelDrop({ state, size = 'md', className }: { state: SignInState; size?: 'md' | 'lg'; className?: string }) {
  const id = useId().replace(/:/g, '');
  return (
    <svg
      aria-hidden
      viewBox="57 -39.6 43.4 27"
      className={cn('fuel-drop', size === 'lg' ? 'h-24' : 'h-14', className)}
      data-state={state}
    >
      <defs>
        <clipPath id={`${id}-drop`}>
          <path d={DROP_PATH} />
        </clipPath>
      </defs>
      <g className="fuel-drop-lines">
        {SPEED_LINES.map((line) => (
          <rect key={line.y} x={LINE_END - line.width} y={line.y} width={line.width} height={LINE_HEIGHT} rx={LINE_HEIGHT / 2} className="fill-accent" />
        ))}
      </g>
      <circle cx="90.12" cy="-23.5" r="13" className="fuel-drop-halo fill-none stroke-accent" strokeWidth="0.8" />
      <path d={DROP_PATH} className="fill-surface-sunken" />
      <g clipPath={`url(#${id}-drop)`}>
        <g className="fuel-drop-level">
          {/* Two wave periods wider than the drop, so sliding by one period loops seamlessly. */}
          <path
            className="fuel-drop-wave fill-accent"
            d="M74 -37.6 q2 -0.9 4 0 t4 0 t4 0 t4 0 t4 0 t4 0 t4 0 t4 0 t4 0 t4 0 V -10 H74 Z"
          />
        </g>
      </g>
      <path d={DROP_PATH} className="fill-none stroke-primary" strokeWidth="0.9" strokeLinejoin="round" />
      <path d={DROP_HIGHLIGHT} className="fuel-drop-shine fill-none stroke-surface" strokeWidth="1.1" strokeLinecap="round" />
      <path d="M86.6 -22.9 l2.5 2.6 l4.8 -5.3" className="fuel-drop-check fill-none stroke-accent-foreground" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" pathLength={1} />
    </svg>
  );
}
