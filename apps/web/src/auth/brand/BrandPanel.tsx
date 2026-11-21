import '@/public/i18n';
import './signin.css';
import { BookOpenCheck, ShieldCheck, UsersRound, type LucideIcon } from 'lucide-react';
import { useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Icon } from '@/design-system';
import { useOnScreen, usePrefersReducedMotion } from '@/public/motion';
import { StageAurora } from '@/public/StageAurora';
import { FillUpCard, StepRail } from './FuelingVignette';
import { useVignettePhase } from './vignette-phase';
import { PANEL_INNER } from './panel-frame';

const TRUTHS: Array<{ id: 'pump' | 'ledger' | 'approval'; icon: LucideIcon }> = [
  { id: 'pump', icon: ShieldCheck },
  { id: 'ledger', icon: BookOpenCheck },
  { id: 'approval', icon: UsersRound },
];

/**
 * The expressive half of the sign-in page: what ZAWED is, told by a fill-up being recorded and
 * three things the product always does. On phones it folds into a short animated band above the
 * form. Its own lazy chunk, so the form never waits for it.
 */
export default function BrandPanel() {
  const { t } = useTranslation('site');
  const reducedMotion = usePrefersReducedMotion();
  const stage = useRef<HTMLDivElement>(null);
  const onScreen = useOnScreen(stage);
  const phase = useVignettePhase(onScreen, reducedMotion);

  return (
    <div ref={stage} className={PANEL_INNER} data-paused={onScreen ? undefined : ''}>
      <StageAurora />
      <div className="relative flex h-full flex-col gap-5 px-4 pt-[4.5rem] pb-12 sm:px-6 lg:justify-between lg:gap-6 lg:p-10 xl:px-14 xl:py-12">
        <div className="max-w-xl">
          <h2 className="public-display text-xl leading-snug font-bold text-balance sm:text-2xl lg:text-[2.25rem] lg:leading-[1.2] xl:text-[2.6rem]">
            {t('panel.title')}
          </h2>
          <p className="mt-4 hidden max-w-lg text-base leading-relaxed text-pretty text-stage-muted lg:[@media(min-height:960px)]:block">
            {t('panel.lead')}
          </p>
        </div>

        <figure className="relative flex flex-col gap-5 lg:mx-auto lg:w-full lg:max-w-[26rem]">
          <figcaption className="sr-only">{t('panel.description')}</figcaption>
          <div aria-hidden className="flex flex-col gap-5">
            <StepRail phase={phase} />
            <div className="relative hidden lg:block">
              <span className="absolute -top-2.5 end-4 z-10 rounded-full border border-stage-line bg-stage px-2.5 py-0.5 text-xs font-medium text-stage-muted">
                {t('panel.illustration')}
              </span>
              <FillUpCard phase={phase} />
            </div>
          </div>
        </figure>

        <ul className="hidden gap-5 lg:grid xl:grid-cols-3 xl:gap-6">
          {TRUTHS.map((truth) => (
            <li key={truth.id} className="flex gap-3 xl:flex-col xl:gap-2.5">
              <span className="flex size-9 shrink-0 items-center justify-center rounded-control bg-stage-panel text-accent ring-1 ring-stage-line">
                <Icon icon={truth.icon} size={18} />
              </span>
              <div className="min-w-0">
                <h3 className="text-sm font-semibold text-stage-foreground">{t(`panel.truths.${truth.id}.title`)}</h3>
                <p className="mt-1 text-sm leading-relaxed text-stage-muted [@media(max-height:879px)]:hidden">{t(`panel.truths.${truth.id}.body`)}</p>
              </div>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}
