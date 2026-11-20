import { cn } from '@/lib/cn';

/** `auto`: on the stage on phones, on the page from lg (the sign-in header). */
export type PublicTone = 'stage' | 'page' | 'auto';

const TONE: Record<PublicTone, string> = {
  stage: 'text-stage-foreground hover:bg-stage-panel',
  page: 'text-foreground hover:bg-surface-sunken',
  auto: 'text-stage-foreground hover:bg-stage-panel lg:text-foreground lg:hover:bg-surface-sunken',
};

/** A compact header control: 40 px high (44 px on touch screens), on the stage or on the page. */
export function publicControlClass(tone: PublicTone, className?: string): string {
  return cn(
    'inline-flex min-h-10 items-center justify-center gap-2 rounded-control px-3 text-sm font-medium transition-colors pointer-coarse:min-h-11',
    TONE[tone],
    className,
  );
}
