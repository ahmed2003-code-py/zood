import { KeyRound, Plus, ShieldOff } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, TextField, toast } from '@/design-system';
import { ListPanel, PageSection } from '@/shell/page';
import { opsApi, type Device, type DeviceActivation } from '../api';
import { DeviceStatusChip } from '../chips';
import { EmptyList, OneTimeSecret, PagedList, ReasonSheet } from '../components';
import { DeviceActivity } from '../device-activity';
import { optional } from '../input';
import { usePagedResource } from '../use-resource';
import { SecretFlowSheet } from './secret-sheet';

/** Left-to-right text inside a translated sentence, isolated so it never reorders the sentence. */
const isolate = (value: string) => `⁨${value}⁩`;

type Dialog = { kind: 'register' } | { kind: 'reissue'; device: Device } | { kind: 'revoke'; device: Device } | undefined;

/** The code a device shows on the POS and the one-time code that activates it. */
function ActivationResult({ activation }: { activation: DeviceActivation }) {
  const { t } = useTranslation('live');
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-1">
        <span className="text-sm font-medium">{t('devices.code')}</span>
        <span dir="ltr" className="font-mono text-lg font-semibold">
          {activation.device.code}
        </span>
      </div>
      <OneTimeSecret label={t('devices.activationCode')} value={activation.activationCode} />
      <p className="text-sm text-muted">{t('devices.activationHint')}</p>
    </div>
  );
}

/** Ops, on a station page: its POS devices, with registering, re-issuing a code and revoking. */
export function DevicesSection({ stationId, closed }: { stationId: string; closed: boolean }) {
  const { t } = useTranslation('live');
  const [dialog, setDialog] = useState<Dialog>();
  const devices = usePagedResource((page, signal) => opsApi.devices.list({ ...page, stationId }, signal), [stationId]);
  const close = () => setDialog(undefined);

  return (
    <PageSection
      title={t('devices.title')}
      action={
        !closed && (
          <Button variant="ghost" size="sm" leadingIcon={Plus} onClick={() => setDialog({ kind: 'register' })}>
            {t('devices.register')}
          </Button>
        )
      }
    >
      <ListPanel>
        <PagedList
          resource={devices}
          empty={
            <EmptyList
              title={t('devices.empty')}
              description={t('devices.emptyHint')}
              action={
                !closed && (
                  <Button leadingIcon={Plus} onClick={() => setDialog({ kind: 'register' })}>
                    {t('devices.register')}
                  </Button>
                )
              }
            />
          }
        >
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((device) => (
                <div key={device.id} className="flex flex-col gap-2 px-4 py-3">
                  <div className="flex items-center justify-between gap-3">
                    <div className="min-w-0">
                      <p dir="ltr" className="text-start font-mono font-medium">
                        {device.code}
                      </p>
                      <p className="text-xs text-muted">{device.label ?? t('devices.noLabel')}</p>
                      <p className="text-xs text-muted">
                        <DeviceActivity device={device} />
                      </p>
                    </div>
                    <DeviceStatusChip status={device.status} />
                  </div>
                  {device.status !== 'REVOKED' && (
                    <div className="flex flex-wrap gap-2">
                      {device.status === 'PENDING_ACTIVATION' && (
                        <Button size="sm" variant="secondary" leadingIcon={KeyRound} onClick={() => setDialog({ kind: 'reissue', device })}>
                          {t('devices.newCode')}
                        </Button>
                      )}
                      <Button size="sm" variant="ghost" leadingIcon={ShieldOff} onClick={() => setDialog({ kind: 'revoke', device })}>
                        {t('devices.revoke')}
                      </Button>
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </PagedList>
      </ListPanel>

      {dialog?.kind === 'register' && <RegisterSheet stationId={stationId} onClose={close} onChanged={devices.reload} />}
      {dialog?.kind === 'reissue' && (
        <SecretFlowSheet<DeviceActivation>
          title={t('devices.newCodeTitle')}
          description={t('devices.newCodeHint')}
          submitLabel={t('devices.newCode')}
          resultTitle={t('devices.newCodeTitle')}
          doneLabel={t('devices.done')}
          form={<p className="text-sm">{t('devices.newCodeConfirm', { code: isolate(dialog.device.code) })}</p>}
          run={() => opsApi.devices.reissueActivation(dialog.device.id)}
          renderResult={(activation) => <ActivationResult activation={activation} />}
          onChanged={devices.reload}
          onClose={close}
        />
      )}
      <ReasonSheet
        open={dialog?.kind === 'revoke'}
        onOpenChange={close}
        title={dialog?.kind === 'revoke' ? t('devices.revokeTitle', { code: isolate(dialog.device.code) }) : ''}
        description={t('devices.revokeHint')}
        submitLabel={t('devices.revoke')}
        submitVariant="danger"
        onConfirm={async (reason) => {
          if (dialog?.kind !== 'revoke') return;
          await opsApi.devices.revoke(dialog.device.id, reason);
          toast(t('devices.revoked'));
          devices.reload();
        }}
      />
    </PageSection>
  );
}

function RegisterSheet({ stationId, onClose, onChanged }: { stationId: string; onClose: () => void; onChanged: () => void }) {
  const { t } = useTranslation('live');
  const [label, setLabel] = useState('');
  return (
    <SecretFlowSheet<DeviceActivation>
      title={t('devices.register')}
      description={t('devices.registerHint')}
      submitLabel={t('devices.register')}
      resultTitle={t('devices.registered')}
      doneLabel={t('devices.done')}
      form={
        <TextField
          label={t('common.optional', { label: t('devices.label') })}
          hint={t('devices.labelHint')}
          maxLength={80}
          value={label}
          onChange={(event) => setLabel(event.target.value)}
        />
      }
      run={() => opsApi.devices.register({ stationId, label: optional(label) })}
      renderResult={(activation) => <ActivationResult activation={activation} />}
      onChanged={onChanged}
      onClose={onClose}
    />
  );
}
