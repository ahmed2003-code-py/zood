import { ChevronDown } from 'lucide-react';
import { Accordion } from 'radix-ui';
import { useTranslation } from 'react-i18next';
import { Icon } from '@/design-system';

const QUESTIONS = ['app', 'offline', 'topUp', 'otherCar', 'where', 'invoice', 'data', 'stationPay', 'try'] as const;

/** Questions and answers in an accordion: each question is a heading with a button (WAI-ARIA pattern). */
export function Faq() {
  const { t } = useTranslation('landing');
  return (
    <section id="faq" aria-labelledby="faq-title" className="scroll-mt-16 py-20 md:py-28">
      <div className="mx-auto grid w-full max-w-6xl gap-10 px-4 md:px-6 lg:grid-cols-[0.8fr_1.2fr] lg:gap-16">
        <h2 id="faq-title" data-reveal className="landing-display text-3xl leading-tight font-bold text-balance md:text-[2.5rem]">
          {t('faq.title')}
        </h2>
        <Accordion.Root data-reveal type="single" collapsible defaultValue="app" className="divide-y divide-border border-y border-border">
          {QUESTIONS.map((id) => (
            <Accordion.Item key={id} value={id}>
              <Accordion.Header asChild>
                <h3>
                  <Accordion.Trigger className="group flex min-h-16 w-full items-center justify-between gap-4 py-4 text-start text-base font-semibold transition-colors hover:text-primary md:text-lg">
                    {t(`faq.items.${id}.q`)}
                    <Icon icon={ChevronDown} size={20} className="text-muted transition-transform duration-200 group-data-[state=open]:rotate-180" />
                  </Accordion.Trigger>
                </h3>
              </Accordion.Header>
              <Accordion.Content className="landing-faq-content overflow-hidden">
                <p className="max-w-2xl pb-5 text-[0.9375rem] leading-relaxed text-muted">{t(`faq.items.${id}.a`)}</p>
              </Accordion.Content>
            </Accordion.Item>
          ))}
        </Accordion.Root>
      </div>
    </section>
  );
}
