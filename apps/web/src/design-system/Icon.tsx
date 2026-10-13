import type { LucideIcon, LucideProps } from 'lucide-react';
import { cn } from '@/lib/cn';

export type { LucideIcon };

interface IconProps extends Omit<LucideProps, 'ref'> {
  icon: LucideIcon;
  /**
   * Directional icons (arrows, chevrons, back) are authored for LTR and mirrored automatically
   * in RTL. Write the LTR meaning: ChevronRight = "forward", ArrowLeft = "back".
   */
  directional?: boolean;
}

export function Icon({ icon: Glyph, directional = false, className, size = 20, ...rest }: IconProps) {
  return (
    <Glyph
      aria-hidden
      focusable={false}
      size={size}
      strokeWidth={1.75}
      className={cn('shrink-0', directional && 'rtl:-scale-x-100', className)}
      {...rest}
    />
  );
}
