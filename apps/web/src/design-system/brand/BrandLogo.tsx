import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/cn';
import { LOGO } from './logo-geometry';

/** Rendered height per size (the full logo is taller because it includes "ZAWED"). */
const HEIGHT = {
  full: { sm: 34, md: 44, lg: 64 },
  compact: { sm: 28, md: 34, lg: 48 },
} as const;

interface BrandLogoProps {
  size?: 'sm' | 'md' | 'lg';
  /** inverse: for brand-green surfaces such as the sidebar and the /demo hero. */
  tone?: 'default' | 'inverse';
  /** compact: the Arabic wordmark only (headers, tight spaces). */
  variant?: 'full' | 'compact';
  className?: string;
}

/**
 * زوّد / ZAWED logo. Colors come from the existing theme tokens, so the logo follows Light/Dark:
 * main shapes use --brand-mark (or --sidebar-active-foreground when inverse), the speed lines,
 * drop reflection and W stroke use --accent. The drop is cut out of the و, so the surface shows
 * through on any background. The mark never mirrors with the page direction.
 */
export function BrandLogo({ size = 'md', tone = 'default', variant = 'full', className }: BrandLogoProps) {
  const { t } = useTranslation();
  const id = useId().replace(/:/g, '');
  const view = variant === 'full' ? LOGO.fullView : LOGO.compactView;
  const height = HEIGHT[variant][size];
  return (
    <svg
      role="img"
      aria-label={t('appName')}
      viewBox={`${view.x} ${view.y} ${view.w} ${view.h}`}
      height={height}
      width={(height * view.w) / view.h}
      className={cn('shrink-0', tone === 'inverse' ? 'text-sidebar-active-foreground' : 'text-brand-mark', className)}
    >
      <defs>
        <mask id={`${id}-drop`} maskUnits="userSpaceOnUse" x={view.x} y={view.y} width={view.w} height={view.h}>
          <rect x={view.x} y={view.y} width={view.w} height={view.h} fill="white" />
          <path d={LOGO.dropPath} fill="black" />
        </mask>
        <clipPath id={`${id}-w`}>
          <polygon points={LOGO.wClip} />
        </clipPath>
      </defs>
      {LOGO.speedLines.map((line) => (
        <rect key={line.y} x={line.x} y={line.y} width={line.w} height={line.h} rx={line.h / 2} className="fill-accent" />
      ))}
      <g mask={`url(#${id}-drop)`} className="fill-current">
        <path d={LOGO.arabicPath} />
        <path d={LOGO.counterPath} className="stroke-current" strokeWidth={1.4} strokeLinejoin="round" />
      </g>
      <path d={LOGO.dropHighlight} fill="none" className="stroke-accent" strokeWidth={LOGO.dropHighlightWidth} strokeLinecap="round" />
      {variant === 'full' && (
        <g transform={LOGO.latinTransform}>
          <path d={LOGO.latinPath} className="fill-current" />
          <path d={LOGO.wPath} className="fill-current" />
          <path d={LOGO.wPath} className="fill-accent" clipPath={`url(#${id}-w)`} />
        </g>
      )}
    </svg>
  );
}

/** Square app icon (W with the shadda) on the primary surface, for compact brand spots. */
export function BrandIcon({ size = 40, className }: { size?: number; className?: string }) {
  const { t } = useTranslation();
  const id = useId().replace(/:/g, '');
  return (
    <svg role="img" aria-label={t('appName')} viewBox="0 0 64 64" width={size} height={size} className={cn('shrink-0', className)}>
      <defs>
        <clipPath id={`${id}-w`}>
          <polygon points={LOGO.icon.wClip} />
        </clipPath>
      </defs>
      <rect width={64} height={64} rx={14} className="fill-primary" />
      <path d={LOGO.icon.shaddaPath} transform={LOGO.icon.shaddaTransform} className="fill-primary-foreground" />
      <g transform={LOGO.icon.wTransform}>
        <path d={LOGO.icon.wPath} className="fill-primary-foreground" />
        <path d={LOGO.icon.wPath} className="fill-accent" clipPath={`url(#${id}-w)`} />
      </g>
    </svg>
  );
}
