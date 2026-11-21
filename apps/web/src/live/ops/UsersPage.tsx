import { Plus, Search, UserRound } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useSession } from '@/auth/session-store';
import { Button, DataView, Icon, ListRow, SegmentedControl, SelectField, TextField, FilterBar, type DataColumn } from '@/design-system';
import { useLocale } from '@/i18n/useLocale';
import { ListPanel, PageHeader } from '@/shell/page';
import { USER_ROLES, adminApi, opsApi, type AdminUser, type UserRole, type UserStatus } from '../api';
import { SCOPED_ROLES } from '../permissions';
import { UserStatusChip } from '../chips';
import { EmptyList, PagedList } from '../components';
import { useDebounced, usePagedResource, useResource } from '../use-resource';
import { UserFormSheet } from './user-form';
import { UserSheets } from './user-sheet';

type StatusFilter = 'ALL' | UserStatus;
type RoleFilter = 'ALL' | UserRole;

/** Ops and Super Admin: the platform's people. Only a Super Admin creates users or changes their status. */
export function UsersPage() {
  const { t } = useTranslation('live');
  const { locale } = useLocale();
  const isSuperAdmin = useSession((state) => state.user?.role) === 'SUPER_ADMIN';
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<StatusFilter>('ALL');
  const [roleFilter, setRoleFilter] = useState<RoleFilter>('ALL');
  const [creating, setCreating] = useState(false);
  const [selected, setSelected] = useState<AdminUser>();
  const [now] = useState(() => Date.now());
  const query = useDebounced(search.trim());
  const users = usePagedResource(
    (page, signal) =>
      adminApi.users.list(
        { ...page, q: query || undefined, status: status === 'ALL' ? undefined : status, role: roleFilter === 'ALL' ? undefined : roleFilter },
        signal,
      ),
    [query, status, roleFilter],
  );
  const companies = useResource((signal) => opsApi.companies.list({ limit: 100 }, signal), []);
  const operators = useResource((signal) => opsApi.operators.list({ limit: 100 }, signal), []);
  const filtered = Boolean(query) || status !== 'ALL' || roleFilter !== 'ALL';

  const organisation = (user: AdminUser): ReactNode => {
    const { type, id } = user.scope;
    if (type === 'PLATFORM') return t('users.scope.PLATFORM');
    const found =
      type === 'COMPANY'
        ? companies.data?.items.find((company) => company.id === id)
        : operators.data?.items.find((operator) => operator.id === id);
    if (found) return locale === 'ar' ? found.nameAr : found.nameEn;
    return <span dir="ltr">{id}</span>;
  };

  const hints = (user: AdminUser): ReactNode => {
    const locked = Boolean(user.lockedUntil) && new Date(user.lockedUntil as string).getTime() > now;
    const parts = [locked && t('users.locked'), user.mustChangePassword && t('users.mustChange')].filter(Boolean);
    return parts.length > 0 ? <p className="text-xs text-warning">{parts.join(t('common.separator'))}</p> : null;
  };

  const columns: DataColumn<AdminUser>[] = [
    {
      id: 'user',
      header: t('users.columns.user'),
      cell: (user) => (
        <div className="min-w-0">
          <p className="font-medium">{user.fullName}</p>
          {hints(user)}
        </div>
      ),
    },
    { id: 'email', header: t('users.columns.email'), cell: (user) => <span dir="ltr">{user.email}</span> },
    { id: 'role', header: t('users.columns.role'), cell: (user) => t(`enums.userRole.${user.role}`) },
    { id: 'organisation', header: t('users.columns.organisation'), cell: organisation },
    { id: 'status', header: t('users.columns.status'), cell: (user) => <UserStatusChip status={user.status} /> },
  ];

  return (
    <>
      <PageHeader
        title={t('users.title')}
        description={t('users.description')}
        stickyActions
        actions={
          isSuperAdmin && (
            <Button leadingIcon={Plus} onClick={() => setCreating(true)}>
              {t('users.new')}
            </Button>
          )
        }
      />
      <ListPanel>
        <FilterBar
          primary={
            <TextField
            className="md:max-w-sm md:flex-1"
            label={t('users.search')}
            leadingIcon={Search}
            type="search"
            data-page-search
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
          }
          activeCount={(status !== 'ALL' ? 1 : 0) + (roleFilter !== 'ALL' ? 1 : 0)}
          onReset={() => (setStatus('ALL'), setRoleFilter('ALL'))}
        >
          <SelectField<RoleFilter>
            className="md:w-56"
            label={t('users.roleFilter')}
            value={roleFilter}
            onValueChange={setRoleFilter}
            // Ops sees and manages company and station users only; platform roles are the Super Admin's.
            options={[
              { value: 'ALL', label: t('common.all') },
              ...(isSuperAdmin ? USER_ROLES : SCOPED_ROLES).map((value) => ({ value, label: t(`enums.userRole.${value}`) })),
            ]}
          />
          <SegmentedControl<StatusFilter>
            label={t('users.statusFilter')}
            value={status}
            onValueChange={setStatus}
            options={[
              { value: 'ALL', label: t('common.all') },
              { value: 'ACTIVE', label: t('enums.userStatus.ACTIVE') },
              { value: 'SUSPENDED', label: t('enums.userStatus.SUSPENDED') },
              { value: 'DEACTIVATED', label: t('enums.userStatus.DEACTIVATED') },
            ]}
          />
        </FilterBar>
        <PagedList
          resource={users}
          empty={
            filtered ? (
              <EmptyList title={t('users.noResults')} description={t('users.noResultsHint')} />
            ) : (
              <EmptyList title={t('users.empty')} description={t('users.emptyHint')} />
            )
          }
        >
          {(items) => (
            <DataView
              caption={t('users.title')}
              rows={items}
              columns={columns}
              getRowId={(user) => user.id}
              onRowClick={setSelected}
              tableFrom="md"
              renderListItem={(user) => (
                <ListRow
                  leading={
                    <span className="flex size-9 items-center justify-center rounded-full bg-primary-subtle text-primary-subtle-foreground">
                      <Icon icon={UserRound} size={18} />
                    </span>
                  }
                  title={user.fullName}
                  subtitle={<span dir="ltr">{user.email}</span>}
                  trailing={<UserStatusChip status={user.status} />}
                  onClick={() => setSelected(user)}
                />
              )}
            />
          )}
        </PagedList>
      </ListPanel>
      {selected && (
        <UserSheets
          key={selected.id}
          user={selected}
          organisation={organisation(selected)}
          isSuperAdmin={isSuperAdmin}
          onClose={() => setSelected(undefined)}
          onChanged={users.reload}
        />
      )}
      {creating && (
        <UserFormSheet
          companies={companies.data?.items ?? []}
          operators={operators.data?.items ?? []}
          onClose={() => setCreating(false)}
          onChanged={users.reload}
        />
      )}
    </>
  );
}
