import { describe, expect, it } from 'vitest';
import type { Agent, Device, Fueling, Shift } from '../api';
import { agentHealth, csvCell, deviceHealth, inShift, piastresToCsv, rangeProblem, shiftMinutes, shiftTotals, shiftsWindow } from './station-model';

const NOW = new Date('2026-11-18T12:00:00Z');

const shift = (overrides: Partial<Shift>): Shift => ({
  id: 's1',
  agentId: 'a1',
  agentName: 'محمود',
  workerId: 'W-100',
  deviceId: 'd1',
  deviceCode: 'POS-201',
  stationId: 'st1',
  startedAt: '2026-11-18T06:00:00Z',
  endedAt: '2026-11-18T10:00:00Z',
  ...overrides,
});

const fueling = (overrides: Partial<Fueling>): Fueling =>
  ({
    id: crypto.randomUUID(),
    deviceId: 'd1',
    agentWorkerId: 'W-100',
    deviceAt: '2026-11-18T07:00:00Z',
    status: 'COMPLETED',
    volumeMl: 20333,
    amountPiastres: 35074,
    ...overrides,
  }) as Fueling;

describe('shift totals', () => {
  it('adds the completed fuelings of the terminal and agent while the shift ran', () => {
    const morning = shift({});
    const evening = shift({ id: 's2', startedAt: '2026-11-18T10:30:00Z', endedAt: undefined });
    const totals = shiftTotals(
      [morning, evening],
      [
        fueling({}),
        fueling({ deviceAt: '2026-11-18T09:59:59Z', volumeMl: 10001, amountPiastres: 17252 }),
        fueling({ status: 'DECLINED', volumeMl: 0, amountPiastres: 0 }),
        fueling({ deviceAt: '2026-11-18T11:00:00Z', volumeMl: 5000, amountPiastres: 8625 }),
        fueling({ agentWorkerId: 'W-101' }),
        fueling({ deviceId: 'd2' }),
      ],
      NOW,
    );
    expect(totals.get('s1')).toEqual({ fuelings: 2, declined: 1, volumeMl: 30334, amountPiastres: 52326 });
    expect(totals.get('s2')).toEqual({ fuelings: 1, declined: 0, volumeMl: 5000, amountPiastres: 8625 });
  });

  it('counts an open shift up to now', () => {
    expect(inShift(shift({ endedAt: undefined }), fueling({ deviceAt: '2026-11-18T11:59:00Z' }), NOW)).toBe(true);
    expect(inShift(shift({ endedAt: undefined }), fueling({ deviceAt: '2026-11-18T12:01:00Z' }), NOW)).toBe(false);
  });

  it('asks for the fuelings from the first start to just after the last end', () => {
    expect(shiftsWindow([shift({}), shift({ startedAt: '2026-11-17T20:00:00Z', endedAt: '2026-11-17T23:00:00Z' })], NOW)).toEqual({
      from: '2026-11-17T20:00:00.000Z',
      to: '2026-11-18T10:00:01.000Z',
    });
    expect(shiftsWindow([], NOW)).toBeUndefined();
  });

  it('measures a shift in whole minutes', () => {
    expect(shiftMinutes(shift({}), NOW)).toBe(240);
    expect(shiftMinutes(shift({ endedAt: undefined }), NOW)).toBe(360);
  });
});

describe('device and agent health', () => {
  const device = (overrides: Partial<Device>) =>
    ({ id: 'd', code: 'POS-1', status: 'ACTIVE', activationExpired: false, lastSyncAt: '2026-11-18T11:50:00Z', ...overrides }) as Device;

  it('tells an expired activation code from one still waiting', () => {
    expect(deviceHealth(device({ status: 'PENDING_ACTIVATION', activationExpiresAt: '2026-11-18T11:00:00Z' }), NOW)).toBe('CODE_EXPIRED');
    expect(deviceHealth(device({ status: 'PENDING_ACTIVATION', activationExpiresAt: '2026-11-18T13:00:00Z' }), NOW)).toBe('AWAITING_ACTIVATION');
    // The server's flag wins when it is sent.
    expect(deviceHealth(device({ status: 'PENDING_ACTIVATION', activationExpiresAt: '2026-11-18T13:00:00Z', activationExpired: true }), NOW)).toBe('CODE_EXPIRED');
  });

  it('calls a terminal offline after 30 minutes without a sync', () => {
    expect(deviceHealth(device({}), NOW)).toBe('ONLINE');
    expect(deviceHealth(device({ lastSyncAt: '2026-11-18T11:00:00Z' }), NOW)).toBe('OFFLINE');
    expect(deviceHealth(device({ lastSyncAt: undefined }), NOW)).toBe('NEVER_SYNCED');
    expect(deviceHealth(device({ status: 'REVOKED' }), NOW)).toBe('REVOKED');
  });

  const agent = (overrides: Partial<Agent>) => ({ id: 'a', status: 'ACTIVE', mustChangePin: false, locked: false, ...overrides }) as Agent;

  it('shows a locked agent as locked, not active, until the lock ends', () => {
    expect(agentHealth(agent({ locked: true, lockedUntil: '2026-11-18T12:10:00Z' }), NOW)).toBe('LOCKED');
    // A lock that ended since the page loaded no longer counts.
    expect(agentHealth(agent({ locked: true, lockedUntil: '2026-11-18T11:10:00Z' }), NOW)).toBe('ACTIVE');
    expect(agentHealth(agent({ locked: true }), NOW)).toBe('LOCKED');
    expect(agentHealth(agent({ mustChangePin: true }), NOW)).toBe('MUST_CHANGE_PIN');
    expect(agentHealth(agent({ status: 'SUSPENDED', locked: true, lockedUntil: '2026-11-18T12:10:00Z' }), NOW)).toBe('SUSPENDED');
  });
});

describe('date ranges', () => {
  it('refuses an end before the start, a future day and more than a year', () => {
    expect(rangeProblem('2026-11-10', '2026-11-01', '2026-11-18')).toBe('FROM_AFTER_TO');
    expect(rangeProblem('2026-11-10', '2026-11-19', '2026-11-18')).toBe('IN_FUTURE');
    expect(rangeProblem('2025-11-01', '2026-11-18', '2026-11-18')).toBe('TOO_LONG');
    expect(rangeProblem('2025-11-18', '2026-11-18', '2026-11-18')).toBeUndefined();
    expect(rangeProblem('', '2026-11-18', '2026-11-18')).toBeUndefined();
  });
});

describe('statement export', () => {
  it('writes piastres as pounds without floating point', () => {
    expect(piastresToCsv(1000075)).toBe('10000.75');
    expect(piastresToCsv(-5)).toBe('-0.05');
    expect(piastresToCsv(0)).toBe('0.00');
  });

  it('quotes cells and defuses formulas', () => {
    expect(csvCell('a,b')).toBe('"a,b"');
    expect(csvCell('=HYPERLINK("x")')).toBe(`"'=HYPERLINK(""x"")"`);
    expect(csvCell('F-10001')).toBe('F-10001');
  });
});
