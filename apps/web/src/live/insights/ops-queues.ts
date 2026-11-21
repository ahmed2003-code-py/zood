/**
 * The Ops home's work queues: what waits for whom, in the order each role works it. An Ops Manager
 * (and the Super Admin) approves money, so approvals come first; Ops verifies transfers and watches
 * alerts, so those come first for them. Queues with work rise to the top; empty ones fold into an
 * "all clear" line. Kept free of React so the ordering is unit-tested.
 */
import type { Role } from '@/auth/types';

export type QueueId =
  | 'approveTopUps'
  | 'verifyTopUps'
  | 'adjustments'
  | 'bankChanges'
  | 'payouts'
  | 'reversals'
  | 'alerts'
  | 'tickets'
  | 'leads'
  | 'expiredDevices';

const APPROVER_ORDER: readonly QueueId[] = ['approveTopUps', 'adjustments', 'payouts', 'bankChanges', 'reversals', 'verifyTopUps', 'alerts', 'tickets', 'leads', 'expiredDevices'];
const OPS_ORDER: readonly QueueId[] = ['verifyTopUps', 'alerts', 'tickets', 'leads', 'expiredDevices', 'bankChanges', 'payouts', 'reversals', 'approveTopUps', 'adjustments'];

export function queueOrder(role: Role | undefined): readonly QueueId[] {
  return role === 'OPS_MANAGER' || role === 'SUPER_ADMIN' ? APPROVER_ORDER : OPS_ORDER;
}

/** Queues this role can act on; the others are shown for awareness only. */
export function canActOn(role: Role | undefined, queue: QueueId): boolean {
  if (queue === 'approveTopUps' || queue === 'adjustments') return role === 'OPS_MANAGER' || role === 'SUPER_ADMIN';
  return true;
}

export interface QueueCount {
  count: number;
  /** The first page was full and another exists: show "99+"-style. */
  more: boolean;
}

export function countOf(page: { items: readonly unknown[]; nextCursor?: string | null }): QueueCount {
  return { count: page.items.length, more: Boolean(page.nextCursor) };
}

/** Queues with work first (in the role's order), then the empty ones; unknown counts stay with the work. */
export function arrangeQueues(order: readonly QueueId[], counts: Partial<Record<QueueId, QueueCount>>): { open: QueueId[]; clear: QueueId[] } {
  const open = order.filter((id) => counts[id] === undefined || counts[id]!.count > 0);
  const clear = order.filter((id) => counts[id] !== undefined && counts[id]!.count === 0);
  return { open, clear };
}

/** "12", or "100+" when the page was full and more exist. */
export function countLabel(count: QueueCount, format: (value: number) => string): string {
  return count.more ? `${format(count.count)}+` : format(count.count);
}
