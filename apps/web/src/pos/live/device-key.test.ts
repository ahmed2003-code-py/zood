import { describe, expect, it } from 'vitest';
import { rawToDer } from './device-key';

describe('rawToDer', () => {
  it('turns a WebCrypto signature into DER that a standard verifier accepts', async () => {
    // Node's own verifier, with DER encoding: the same form Java's SHA256withECDSA reads. Imported
    // by name so the browser build's type settings need no Node types.
    const node = (await import(/* @vite-ignore */ 'node:' + 'crypto')) as {
      createPublicKey: (input: { key: Uint8Array; format: 'der'; type: 'spki' }) => unknown;
      verify: (algorithm: string, data: Uint8Array, key: { key: unknown; dsaEncoding: 'der' }, signature: Uint8Array) => boolean;
    };
    const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify']);
    const spki = new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey));
    const publicKey = node.createPublicKey({ key: spki, format: 'der', type: 'spki' });
    for (let i = 0; i < 20; i++) {
      const message = new TextEncoder().encode(`zawed-device-token\ndevice\n${i}\nnonce-${i}`);
      const raw = new Uint8Array(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, pair.privateKey, message));
      const der = rawToDer(raw);
      expect(node.verify('sha256', message, { key: publicKey, dsaEncoding: 'der' }, der)).toBe(true);
    }
  });

  it('keeps integers positive and minimal', () => {
    const raw = new Uint8Array(64);
    raw[0] = 0x80; // r has its high bit set: needs a leading zero
    raw[63] = 0x01; // s is 1: all leading zeros trimmed
    expect(Array.from(rawToDer(raw).subarray(0, 4))).toEqual([0x30, 2 + 33 + 3, 0x02, 33]);
    expect(Array.from(rawToDer(raw).slice(-3))).toEqual([0x02, 0x01, 0x01]);
  });

  it('refuses anything that is not a P-256 signature', () => {
    expect(() => rawToDer(new Uint8Array(63))).toThrow();
  });
});
