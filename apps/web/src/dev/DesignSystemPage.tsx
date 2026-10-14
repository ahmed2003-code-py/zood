import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { LanguageSwitcher, SegmentedControl, ThemeSwitcher, BrandLogo } from '@/design-system';
import { readStorage, STORAGE_KEYS, writeStorage } from '@/lib/storage';
import { CANVAS_HEIGHT_MESSAGE } from './messages';

const VIEWPORTS = ['390', '768', '1440'] as const;
type Viewport = (typeof VIEWPORTS)[number];
type View = 'single' | 'matrix';

const COMBINATIONS = [
  { key: 'ar-light', lang: 'ar', theme: 'light' },
  { key: 'ar-dark', lang: 'ar', theme: 'dark' },
  { key: 'en-light', lang: 'en', theme: 'light' },
  { key: 'en-dark', lang: 'en', theme: 'dark' },
] as const;

/**
 * Renders the canvas in an iframe at an exact viewport width so responsive breakpoints really
 * apply, scaled down to fit the screen when needed.
 */
function PreviewFrame({ src, width, title }: { src: string; width: number; title: string }) {
  const wrapperRef = useRef<HTMLDivElement>(null);
  const frameRef = useRef<HTMLIFrameElement>(null);
  const [available, setAvailable] = useState(width);
  const [height, setHeight] = useState(900);

  useEffect(() => {
    const wrapper = wrapperRef.current;
    if (!wrapper) return;
    const observer = new ResizeObserver(([entry]) => {
      if (entry) setAvailable(entry.contentRect.width);
    });
    observer.observe(wrapper);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    const onMessage = (event: MessageEvent) => {
      const data = event.data as { type?: string; height?: number } | null;
      if (event.source === frameRef.current?.contentWindow && data?.type === CANVAS_HEIGHT_MESSAGE && data.height) {
        setHeight(data.height);
      }
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, []);

  const scale = Math.min(1, available / width);
  return (
    // The frame is scaled from its top-left corner, so this wrapper is laid out LTR in both languages.
    <div ref={wrapperRef} dir="ltr" className="w-full">
      <div
        className="mx-auto overflow-hidden rounded-container border border-border bg-background"
        style={{ width: width * scale, height: height * scale }}
      >
        <iframe
          ref={frameRef}
          src={src}
          title={title}
          className="block border-0"
          style={{ width, height, transform: `scale(${scale})`, transformOrigin: 'top left' }}
        />
      </div>
    </div>
  );
}

export function DesignSystemPage() {
  const { t } = useTranslation('dev');
  const [viewport, setViewport] = useState<Viewport>(() => {
    const stored = readStorage(STORAGE_KEYS.devViewport);
    return VIEWPORTS.find((value) => value === stored) ?? '390';
  });
  const [view, setView] = useState<View>('single');

  const changeViewport = (next: Viewport) => {
    setViewport(next);
    writeStorage(STORAGE_KEYS.devViewport, next);
  };
  const width = Number(viewport);

  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-10 border-b border-border bg-surface">
        <div className="mx-auto flex max-w-[1600px] flex-wrap items-center gap-x-6 gap-y-3 px-4 py-3">
          <div className="flex items-center gap-3">
            <BrandLogo size="sm" variant="compact" />
            <div>
              <h1 className="text-base font-semibold">{t('title')}</h1>
              <p className="hidden text-xs text-muted lg:block">{t('subtitle')}</p>
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2 lg:ms-auto">
            <LanguageSwitcher />
            <ThemeSwitcher />
            <SegmentedControl
              label={t('toolbar.width')}
              value={viewport}
              onValueChange={changeViewport}
              options={VIEWPORTS.map((value) => ({ value, label: value }))}
            />
            <SegmentedControl
              label={t('toolbar.view')}
              value={view}
              onValueChange={setView}
              options={[
                { value: 'single', label: t('toolbar.single') },
                { value: 'matrix', label: t('toolbar.matrix') },
              ]}
            />
          </div>
        </div>
      </header>
      <main className="mx-auto max-w-[1600px] p-4">
        {view === 'single' ? (
          <PreviewFrame key={viewport} src="/dev/ui/canvas" width={width} title={t('frameTitle')} />
        ) : (
          <div className="grid gap-6 lg:grid-cols-2">
            {COMBINATIONS.map((combination) => (
              <figure key={combination.key} className="flex min-w-0 flex-col gap-2">
                <figcaption className="text-sm font-semibold">{t(`matrix.${combination.key}`)}</figcaption>
                <PreviewFrame
                  key={viewport}
                  src={`/dev/ui/canvas?lang=${combination.lang}&theme=${combination.theme}`}
                  width={width}
                  title={t(`matrix.${combination.key}`)}
                />
              </figure>
            ))}
          </div>
        )}
      </main>
    </div>
  );
}
