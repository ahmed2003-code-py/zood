import { KeyRound, Pause, Plus, Sun, UserX } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, TextField, toast } from '@/design-system';
import { ListPanel, PageSection } from '@/shell/page';
import { opsApi, type Agent, type AgentStatus, type AgentWithPin } from '../api';
import { AgentStatusChip } from '../chips';
import { EmptyList, OneTimeSecret, PagedList, ReasonSheet } from '../components';
import { optional } from '../input';
import { usePagedResource } from '../use-resource';
import { SecretFlowSheet } from './secret-sheet';

/** Left-to-right text inside a translated sentence, isolated so it never reorders the sentence. */
const isolate = (value: string) => `⁨${value}⁩`;

type Dialog = { kind: 'add' } | { kind: 'resetPin'; agent: Agent } | { kind: 'status'; agent: Agent; to: AgentStatus } | undefined;

/** The worker ID the agent signs in with, and the PIN that was just generated for them. */
function PinResult({ result }: { result: AgentWithPin }) {
  const { t } = useTranslation('live');
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-1">
        <span className="text-sm font-medium">{t('agents.workerId')}</span>
        <span dir="ltr" className="font-mono text-lg font-semibold">
          {result.agent.workerId}
        </span>
      </div>
      <OneTimeSecret label={t('agents.pin')} value={result.pin} group={3} />
      <p className="text-sm text-muted">{t('agents.pinHint')}</p>
    </div>
  );
}

/** Ops, on a station page: the station's pump agents, with PINs, suspension and deactivation. */
export function AgentsSection({ stationId, closed }: { stationId: string; closed: boolean }) {
  const { t } = useTranslation('live');
  const [dialog, setDialog] = useState<Dialog>();
  const agents = usePagedResource((page, signal) => opsApi.agents.list({ ...page, stationId }, signal), [stationId]);
  const close = () => setDialog(undefined);

  return (
    <PageSection
      title={t('agents.title')}
      action={
        !closed && (
          <Button variant="ghost" size="sm" leadingIcon={Plus} onClick={() => setDialog({ kind: 'add' })}>
            {t('agents.add')}
          </Button>
        )
      }
    >
      <ListPanel>
        <PagedList
          resource={agents}
          empty={
            <EmptyList
              title={t('agents.empty')}
              description={t('agents.emptyHint')}
              action={
                !closed && (
                  <Button leadingIcon={Plus} onClick={() => setDialog({ kind: 'add' })}>
                    {t('agents.add')}
                  </Button>
                )
              }
            />
          }
        >
          {(items) => (
            <div className="divide-y divide-border">
              {items.map((agent) => (
                <div key={agent.id} className="flex flex-col gap-2 px-4 py-3">
                  <div className="flex items-center justify-between gap-3">
                    <div className="min-w-0">
                      <p className="font-medium">{agent.fullName}</p>
                      <p dir="ltr" className="text-start font-mono text-xs text-muted">
                        {agent.workerId}
                      </p>
                      {agent.mustChangePin && agent.status !== 'DEACTIVATED' && (
                        <p className="text-xs text-muted">{t('agents.mustChangePin')}</p>
                      )}
                    </div>
                    <AgentStatusChip status={agent.status} />
                  </div>
                  {agent.status !== 'DEACTIVATED' && (
                    <div className="flex flex-wrap gap-2">
                      <Button size="sm" variant="secondary" leadingIcon={KeyRound} onClick={() => setDialog({ kind: 'resetPin', agent })}>
                        {t('agents.resetPin')}
                      </Button>
                      {agent.status === 'ACTIVE' ? (
                        <Button size="sm" variant="ghost" leadingIcon={Pause} onClick={() => setDialog({ kind: 'status', agent, to: 'SUSPENDED' })}>
                          {t('agents.suspend')}
                        </Button>
                      ) : (
                        <Button size="sm" variant="ghost" leadingIcon={Sun} onClick={() => setDialog({ kind: 'status', agent, to: 'ACTIVE' })}>
                          {t('agents.reactivate')}
                        </Button>
                      )}
                      <Button size="sm" variant="ghost" leadingIcon={UserX} onClick={() => setDialog({ kind: 'status', agent, to: 'DEACTIVATED' })}>
                        {t('agents.deactivate')}
                      </Button>
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </PagedList>
      </ListPanel>

      {dialog?.kind === 'add' && <AddAgentSheet stationId={stationId} onClose={close} onChanged={agents.reload} />}
      {dialog?.kind === 'resetPin' && (
        <SecretFlowSheet<AgentWithPin>
          title={t('agents.resetPinTitle', { name: dialog.agent.fullName })}
          description={t('agents.resetPinHint')}
          submitLabel={t('agents.resetPin')}
          resultTitle={t('agents.resetPinDone')}
          doneLabel={t('agents.done')}
          form={<p className="text-sm">{t('agents.resetPinConfirm', { workerId: isolate(dialog.agent.workerId) })}</p>}
          run={() => opsApi.agents.resetPin(dialog.agent.id)}
          renderResult={(result) => <PinResult result={result} />}
          onChanged={agents.reload}
          onClose={close}
        />
      )}
      <ReasonSheet
        open={dialog?.kind === 'status'}
        onOpenChange={close}
        title={
          dialog?.kind === 'status'
            ? t(
                dialog.to === 'SUSPENDED' ? 'agents.suspendTitle' : dialog.to === 'ACTIVE' ? 'agents.reactivateTitle' : 'agents.deactivateTitle',
                { name: dialog.agent.fullName },
              )
            : ''
        }
        description={
          dialog?.kind === 'status'
            ? t(dialog.to === 'SUSPENDED' ? 'agents.suspendHint' : dialog.to === 'ACTIVE' ? 'agents.reactivateHint' : 'agents.deactivateHint')
            : undefined
        }
        submitLabel={
          dialog?.kind === 'status'
            ? t(dialog.to === 'SUSPENDED' ? 'agents.suspend' : dialog.to === 'ACTIVE' ? 'agents.reactivate' : 'agents.deactivate')
            : ''
        }
        submitVariant={dialog?.kind === 'status' && dialog.to !== 'ACTIVE' ? 'danger' : 'primary'}
        onConfirm={async (reason) => {
          if (dialog?.kind !== 'status') return;
          await opsApi.agents.setStatus(dialog.agent.id, { status: dialog.to, reason });
          toast(t('agents.statusChanged'));
          agents.reload();
        }}
      />
    </PageSection>
  );
}

function AddAgentSheet({ stationId, onClose, onChanged }: { stationId: string; onClose: () => void; onChanged: () => void }) {
  const { t } = useTranslation('live');
  const [fullName, setFullName] = useState('');
  const [phone, setPhone] = useState('');
  const [errors, setErrors] = useState<{ fullName?: string; phone?: string }>({});
  return (
    <SecretFlowSheet<AgentWithPin>
      title={t('agents.add')}
      description={t('agents.addHint')}
      submitLabel={t('agents.add')}
      resultTitle={t('agents.added')}
      doneLabel={t('agents.done')}
      form={
        <>
          <TextField
            label={t('agents.fullName')}
            maxLength={200}
            autoComplete="off"
            value={fullName}
            error={errors.fullName}
            onChange={(event) => {
              setFullName(event.target.value);
              setErrors({});
            }}
          />
          <TextField
            label={t('common.optional', { label: t('agents.phone') })}
            maxLength={30}
            type="tel"
            dir="ltr"
            value={phone}
            error={errors.phone}
            onChange={(event) => setPhone(event.target.value)}
          />
        </>
      }
      run={async () => {
        if (!fullName.trim()) {
          setErrors({ fullName: t('errors.required') });
          return false;
        }
        return opsApi.agents.create({ stationId, fullName: fullName.trim(), phone: optional(phone) });
      }}
      onError={(error) => {
        if (error.field === 'fullName') setErrors({ fullName: error.message });
        if (error.field === 'phone') setErrors({ phone: error.message });
      }}
      renderResult={(result) => <PinResult result={result} />}
      onChanged={onChanged}
      onClose={onClose}
    />
  );
}
