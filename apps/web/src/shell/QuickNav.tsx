/**
 * Keyboard shortcuts for people who live in the portal all day:
 * - "/" puts the cursor in the page's search box (an input marked data-page-search);
 * - Ctrl+K or ⌘K opens a small "go to" list of the portal's destinations, filtered as you type.
 * Neither fires while the person is typing in a field.
 */
import { CornerDownLeft, Search } from 'lucide-react';
import { Dialog } from 'radix-ui';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { Icon, fieldControlClass } from '@/design-system';
import { cn } from '@/lib/cn';
import type { PortalConfig } from './types';
import { useNavGroups } from './use-nav-groups';

function typingIn(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
}

/** The first page search box that is on screen (the phone and desktop copies of a filter bar both exist). */
function visibleSearch(): HTMLInputElement | undefined {
  return [...document.querySelectorAll<HTMLInputElement>('input[data-page-search]')].find((input) => input.offsetParent !== null);
}

export function QuickNav({ config }: { config: PortalConfig }) {
  const { t } = useTranslation(['shell', 'navigation']);
  const navigate = useNavigate();
  const items = useNavGroups(config).flatMap((group) => group.items);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        setQuery('');
        setOpen((value) => !value);
        return;
      }
      if (event.key === '/' && !event.ctrlKey && !event.metaKey && !event.altKey && !typingIn(event.target)) {
        const search = visibleSearch();
        if (search) {
          event.preventDefault();
          search.focus();
          search.select();
        }
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const needle = query.trim().toLocaleLowerCase();
  const matches = items.filter((item) => !needle || t(`navigation:items.${item.id}`).toLocaleLowerCase().includes(needle));
  const go = (path: string) => {
    setOpen(false);
    navigate(path);
  };

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-overlay animate-[zood-fade-in_150ms_ease-out]" />
        <Dialog.Content className="fixed inset-x-4 top-[12vh] z-50 mx-auto max-w-md overflow-clip rounded-dialog border border-border bg-surface-elevated text-foreground shadow-dialog animate-[zood-dialog-in_180ms_cubic-bezier(0.22,1,0.36,1)] focus:outline-none">
          <Dialog.Title className="sr-only">{t('shell:quickNav.title')}</Dialog.Title>
          <Dialog.Description className="sr-only">{t('shell:quickNav.hint')}</Dialog.Description>
          <form
            className="relative border-b border-border p-3"
            onSubmit={(event) => {
              event.preventDefault();
              if (matches[0]) go(matches[0].path);
            }}
          >
            <span className="pointer-events-none absolute inset-y-0 start-6 flex items-center text-muted">
              <Icon icon={Search} size={18} />
            </span>
            <input
              autoFocus
              aria-label={t('shell:quickNav.title')}
              placeholder={t('shell:quickNav.placeholder')}
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              className={cn(fieldControlClass(false), 'min-h-11 w-full ps-10')}
            />
          </form>
          <ul className="max-h-80 overflow-y-auto p-2">
            {matches.length === 0 && <li className="px-3 py-6 text-center text-sm text-muted">{t('shell:quickNav.none')}</li>}
            {matches.map((item, index) => (
              <li key={item.id}>
                <button
                  type="button"
                  onClick={() => go(item.path)}
                  className="flex min-h-11 w-full items-center gap-3 rounded-control px-3 text-start text-sm font-medium transition-colors duration-150 hover:bg-surface-hover focus-visible:bg-surface-hover"
                >
                  <span className="flex size-8 items-center justify-center rounded-control bg-primary-subtle text-primary-subtle-foreground">
                    <Icon icon={item.icon} size={16} />
                  </span>
                  <span className="flex-1">{t(`navigation:items.${item.id}`)}</span>
                  {index === 0 && needle && <Icon icon={CornerDownLeft} size={14} className="text-muted" />}
                </button>
              </li>
            ))}
          </ul>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
