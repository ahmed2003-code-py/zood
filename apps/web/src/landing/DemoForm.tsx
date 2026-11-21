import { CircleCheck, Mail, MessageCircle, Send } from 'lucide-react';
import { useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { api } from '@/api';
import { Banner, Button, Icon, SelectField, TextField } from '@/design-system';
import {
  DEMO_LIMITS,
  FLEET_SIZES,
  normalizeEgyptianMobile,
  validateDemoRequest,
  type DemoField,
  type DemoFieldError,
  type DemoRequestInput,
  type FleetSize,
} from '@/domain/demo-request';
import { useLocale } from '@/i18n/useLocale';
import { CONTACT, mailtoUrl } from './contact';
import { buildDemoRequest, describeSendFailure, type SendFailure } from './demo-form-model';

type TextFields = Omit<DemoRequestInput, 'fleetSize'>;
const EMPTY: TextFields = { companyName: '', contactName: '', phone: '', email: '' };

/** First-strong isolates keep a Latin name or a phone number in order inside an Arabic sentence. */
const isolate = (value: string) => `⁨${value}⁩`;

interface DemoFormProps {
  fleetSize: FleetSize | '';
  onFleetSizeChange: (value: FleetSize | '') => void;
}

/**
 * "Request a demo": client validation with the server's rules, accessible errors, a honeypot and
 * the render time for spam checks, then a success state or an explained failure.
 */
export function DemoForm({ fleetSize, onFleetSizeChange }: DemoFormProps) {
  const { t } = useTranslation('landing');
  const { locale } = useLocale();
  const form = useRef<HTMLFormElement>(null);
  const [values, setValues] = useState<TextFields>(EMPTY);
  const [errors, setErrors] = useState<Partial<Record<DemoField, DemoFieldError>>>({});
  const [serverFields, setServerFields] = useState<DemoField[]>([]);
  const [attempted, setAttempted] = useState(false);
  const [sending, setSending] = useState(false);
  const [failure, setFailure] = useState<SendFailure>();
  const [sent, setSent] = useState<{ name: string; phone: string }>();
  // When the form appeared: a request sent a second later is a bot, and the server knows it.
  const [startedAt, setStartedAt] = useState(() => Date.now());

  const input = (next: TextFields, nextFleet: FleetSize | '' = fleetSize) => {
    // Once a send was attempted, errors follow the typing so the visitor sees each one clear.
    if (attempted) {
      const result = validateDemoRequest({ ...next, fleetSize: nextFleet });
      setErrors(result.ok ? {} : result.errors);
    }
  };

  const change = (field: keyof TextFields, value: string) => {
    const next = { ...values, [field]: value };
    setValues(next);
    setServerFields((fields) => fields.filter((item) => item !== field));
    input(next);
  };

  const message = (field: DemoField): string | undefined => {
    const error = errors[field];
    if (error) {
      if (field === 'fleetSize' && error === 'required') return t('demo.errors.fleetRequired');
      const limits = field === 'companyName' || field === 'contactName' ? DEMO_LIMITS[field] : DEMO_LIMITS.email;
      return t(`demo.errors.${error}`, { min: 'min' in limits ? limits.min : 0, max: limits.max });
    }
    return serverFields.includes(field) ? t('demo.errors.server') : undefined;
  };

  const focusFirstInvalid = () =>
    requestAnimationFrame(() => form.current?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus());

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (sending) return;
    setAttempted(true);
    const result = validateDemoRequest({ ...values, fleetSize });
    if (!result.ok) {
      setErrors(result.errors);
      focusFirstInvalid();
      return;
    }
    setErrors({});
    setFailure(undefined);
    setSending(true);
    const website = (event.currentTarget.elements.namedItem('website') as HTMLInputElement | null)?.value ?? '';
    try {
      await api.demoRequests.submit(buildDemoRequest(result.values, locale, { website, startedAt }));
      setSent({ name: result.values.contactName, phone: result.values.phone });
    } catch (error) {
      const described = describeSendFailure(error);
      setFailure(described);
      if (described.kind === 'validation') {
        setServerFields(described.fields);
        focusFirstInvalid();
      }
    } finally {
      setSending(false);
    }
  };

  const reset = () => {
    setValues(EMPTY);
    onFleetSizeChange('');
    setErrors({});
    setServerFields([]);
    setAttempted(false);
    setFailure(undefined);
    setSent(undefined);
    setStartedAt(Date.now());
  };

  if (sent) {
    return (
      <div role="status" className="flex flex-col items-start gap-4 rounded-container border border-success/30 bg-success-subtle p-6 md:p-8">
        <Icon icon={CircleCheck} size={36} className="text-success" />
        <h3
          tabIndex={-1}
          ref={(heading) => heading?.focus()}
          className="text-xl font-semibold outline-none"
        >
          {t('demo.success.title', { name: isolate(sent.name) })}
        </h3>
        <p className="text-[0.9375rem] leading-relaxed text-muted">{t('demo.success.body', { phone: isolate(sent.phone) })}</p>
        <div className="flex flex-wrap gap-2">
          <Button asChild>
            <a href={CONTACT.whatsappUrl} target="_blank" rel="noopener noreferrer">
              <Icon icon={MessageCircle} size={18} />
              {t('demo.direct.whatsapp')}
            </a>
          </Button>
          <Button variant="secondary" onClick={reset}>
            {t('demo.success.again')}
          </Button>
        </div>
      </div>
    );
  }

  return (
    <form ref={form} noValidate onSubmit={submit} aria-labelledby="demo-title" className="relative flex flex-col gap-5">
      {attempted && Object.keys(errors).length > 0 && (
        <p role="alert" className="text-sm font-semibold text-danger">
          {t('demo.errors.summary')}
        </p>
      )}
      {failure && <FailureBanner failure={failure} />}
      <div className="grid gap-5 sm:grid-cols-2">
        <TextField
          name="companyName"
          label={t('demo.fields.companyName')}
          autoComplete="organization"
          maxLength={DEMO_LIMITS.companyName.max + 20}
          value={values.companyName}
          error={message('companyName')}
          onChange={(event) => change('companyName', event.target.value)}
          className="sm:col-span-2"
        />
        <TextField
          name="contactName"
          label={t('demo.fields.contactName')}
          autoComplete="name"
          maxLength={DEMO_LIMITS.contactName.max + 20}
          value={values.contactName}
          error={message('contactName')}
          onChange={(event) => change('contactName', event.target.value)}
        />
        <TextField
          name="phone"
          type="tel"
          inputMode="tel"
          dir="ltr"
          autoComplete="tel-national"
          label={t('demo.fields.phone')}
          hint={t('demo.fields.phoneHint')}
          value={values.phone}
          error={message('phone')}
          onChange={(event) => change('phone', event.target.value)}
          // Show the number as it will be stored (Arabic digits and +20 become 010…).
          onBlur={() => {
            const normalized = normalizeEgyptianMobile(values.phone);
            if (normalized && normalized !== values.phone) change('phone', normalized);
          }}
        />
        <TextField
          name="email"
          type="email"
          inputMode="email"
          dir="ltr"
          autoComplete="email"
          label={t('demo.fields.email')}
          maxLength={DEMO_LIMITS.email.max + 20}
          value={values.email}
          error={message('email')}
          onChange={(event) => change('email', event.target.value)}
        />
        <SelectField<FleetSize | ''>
          label={t('demo.fields.fleetSize')}
          value={fleetSize}
          error={message('fleetSize')}
          onValueChange={(value) => {
            onFleetSizeChange(value);
            setServerFields((fields) => fields.filter((item) => item !== 'fleetSize'));
            input(values, value);
          }}
          options={[
            { value: '', label: t('demo.fields.fleetChoose') },
            ...FLEET_SIZES.map((value) => ({ value, label: t(`demo.fleetSizes.${value}`) })),
          ]}
        />
      </div>

      {/* Honeypot: invisible to people and skipped by the keyboard; bots fill every field. */}
      <div aria-hidden className="absolute -start-[200vw] top-0 size-px overflow-hidden">
        <label>
          {t('demo.fields.website')}
          <input type="text" name="website" tabIndex={-1} autoComplete="off" defaultValue="" />
        </label>
      </div>

      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <p className="text-xs leading-relaxed text-muted">
          {t('demo.privacy')}{' '}
          <Link to="/privacy" className="font-medium text-foreground underline decoration-border-strong underline-offset-2 hover:decoration-current">
            {t('demo.privacyLink')}
          </Link>
        </p>
        <Button type="submit" size="lg" loading={sending} trailingIcon={Send} trailingIconDirectional className="sm:min-w-44">
          {t('demo.submit')}
        </Button>
      </div>
    </form>
  );
}

function FailureBanner({ failure }: { failure: SendFailure }) {
  const { t } = useTranslation('landing');
  switch (failure.kind) {
    case 'rateLimited':
      return (
        <Banner
          tone="warning"
          title={t('demo.failure.rateLimitedTitle')}
          description={failure.retryMinutes ? t('demo.failure.rateLimited', { count: failure.retryMinutes }) : t('demo.failure.rateLimitedSoon')}
          action={<WhatsAppLink />}
        />
      );
    case 'validation':
      return <Banner tone="danger" title={t('demo.failure.validationTitle')} description={t('demo.failure.validation')} />;
    case 'network':
      return <Banner tone="danger" title={t('demo.failure.networkTitle')} description={t('demo.failure.network')} action={<WhatsAppLink />} />;
    default:
      return (
        <Banner
          tone="danger"
          title={t('demo.failure.genericTitle')}
          description={[t('demo.failure.generic'), failure.requestId && t('demo.failure.reference', { requestId: failure.requestId })]
            .filter(Boolean)
            .join(' ')}
          action={<WhatsAppLink />}
        />
      );
  }
}

function WhatsAppLink() {
  const { t } = useTranslation('landing');
  return (
    <Button asChild variant="secondary" size="sm">
      <a href={CONTACT.whatsappUrl} target="_blank" rel="noopener noreferrer">
        <Icon icon={MessageCircle} size={16} />
        {t('demo.direct.whatsapp')}
      </a>
    </Button>
  );
}

/** WhatsApp and e-mail, next to the form. */
export function DirectContact() {
  const { t } = useTranslation('landing');
  return (
    <div className="flex flex-col gap-3">
      <h3 className="text-base font-semibold">{t('demo.direct.title')}</h3>
      <a
        href={CONTACT.whatsappUrl}
        target="_blank"
        rel="noopener noreferrer"
        className="group flex items-center gap-3 rounded-control border border-border bg-background p-3 transition-colors hover:border-primary"
      >
        <span className="flex size-10 items-center justify-center rounded-control bg-primary-subtle text-primary-subtle-foreground">
          <Icon icon={MessageCircle} size={20} />
        </span>
        <span className="flex flex-col">
          <span className="text-sm text-muted">{t('demo.direct.whatsapp')}</span>
          <span dir="ltr" className="text-start font-semibold tabular-nums">
            {CONTACT.whatsappDisplay}
          </span>
        </span>
      </a>
      <a
        href={mailtoUrl(t('demo.direct.emailSubject'))}
        className="group flex items-center gap-3 rounded-control border border-border bg-background p-3 transition-colors hover:border-primary"
      >
        <span className="flex size-10 items-center justify-center rounded-control bg-primary-subtle text-primary-subtle-foreground">
          <Icon icon={Mail} size={20} />
        </span>
        <span className="flex min-w-0 flex-col">
          <span className="text-sm text-muted">{t('demo.direct.email')}</span>
          <span dir="ltr" className="truncate text-start font-semibold">
            {CONTACT.email}
          </span>
        </span>
      </a>
      <p className="text-sm text-muted">{t('demo.direct.languages')}</p>
    </div>
  );
}
