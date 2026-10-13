import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/cn';

// Brand marks are fixed artwork, not translatable UI text.
const BRAND_ARABIC = 'زُود';
const BRAND_LATIN = 'ZOOD';

const SIZE = {
  sm: 'text-base',
  md: 'text-xl',
  lg: 'text-3xl',
} as const;

interface ZoodWordmarkProps {
  size?: keyof typeof SIZE;
  /** inverse: for dark green surfaces such as the sidebar. */
  tone?: 'default' | 'inverse';
  showLatin?: boolean;
  className?: string;
}

/**
 * Interim typographic wordmark based on the official logo (speed lines + "زُود" + "ZOOD").
 * Replace the internals with the official SVG once it is delivered; keep this component's API.
 * The mark never mirrors with the page direction.
 */
export function ZoodWordmark({ size = 'md', tone = 'default', showLatin = true, className }: ZoodWordmarkProps) {
  const { t } = useTranslation();
  return (
    <span
      role="img"
      aria-label={t('appName')}
      dir="ltr"
      className={cn(
        'inline-flex flex-col items-center leading-none select-none',
        SIZE[size],
        tone === 'inverse' ? 'text-sidebar-active-foreground' : 'text-brand-mark',
        className,
      )}
    >
      <span className="inline-flex items-center gap-[0.18em]">
        <span aria-hidden className="flex flex-col items-end gap-[0.09em]">
          <span className="h-[0.11em] w-[0.55em] rounded-full bg-accent" />
          <span className="h-[0.11em] w-[0.8em] rounded-full bg-accent" />
          <span className="h-[0.11em] w-[0.45em] rounded-full bg-accent" />
        </span>
        <span aria-hidden lang="ar" className="text-[1.5em] font-bold">
          {BRAND_ARABIC}
        </span>
      </span>
      {showLatin && (
        <span aria-hidden lang="en" className="mt-[0.15em] text-[0.42em] font-semibold tracking-[0.35em]">
          {BRAND_LATIN}
        </span>
      )}
    </span>
  );
}
