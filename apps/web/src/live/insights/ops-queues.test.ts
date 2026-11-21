import { describe, expect, it } from 'vitest';
import { arrangeQueues, canActOn, countLabel, countOf, queueOrder } from './ops-queues';

describe('Ops work queues', () => {
  it('puts approvals first for the Ops Manager and verification first for Ops', () => {
    expect(queueOrder('OPS_MANAGER')[0]).toBe('approveTopUps');
    expect(queueOrder('SUPER_ADMIN')[0]).toBe('approveTopUps');
    expect(queueOrder('OPS')[0]).toBe('verifyTopUps');
  });

  it('lets only approvers act on money approvals', () => {
    expect(canActOn('OPS', 'approveTopUps')).toBe(false);
    expect(canActOn('OPS', 'adjustments')).toBe(false);
    expect(canActOn('OPS', 'bankChanges')).toBe(true);
    expect(canActOn('OPS_MANAGER', 'approveTopUps')).toBe(true);
  });

  it('lifts queues with work and folds empty ones away, keeping the role order', () => {
    const arranged = arrangeQueues(['approveTopUps', 'verifyTopUps', 'alerts', 'leads'], {
      approveTopUps: { count: 0, more: false },
      verifyTopUps: { count: 3, more: false },
      leads: { count: 1, more: false },
    });
    expect(arranged).toEqual({ open: ['verifyTopUps', 'alerts', 'leads'], clear: ['approveTopUps'] });
  });

  it('counts a page and says when more exist', () => {
    expect(countOf({ items: [1, 2, 3], nextCursor: 'x' })).toEqual({ count: 3, more: true });
    expect(countLabel({ count: 100, more: true }, String)).toBe('100+');
    expect(countLabel({ count: 4, more: false }, String)).toBe('4');
  });
});
