/**
 * The POS device's own key pair (ADR 0010). The private key is created by WebCrypto as
 * non-extractable: script on this page can ask it to sign, but nobody can read it out, and it is
 * kept in IndexedDB on this browser only. The public key is sent once, at activation.
 */

const DB_NAME = 'zawed-pos-device';
const STORE = 'keys';
const KEY_ID = 'device';
const ALGORITHM: EcKeyGenParams = { name: 'ECDSA', namedCurve: 'P-256' };

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, 1);
    request.onupgradeneeded = () => request.result.createObjectStore(STORE);
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function withStore<T>(mode: IDBTransactionMode, run: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await openDb();
  try {
    return await new Promise<T>((resolve, reject) => {
      const request = run(db.transaction(STORE, mode).objectStore(STORE));
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  } finally {
    db.close();
  }
}

export async function loadKeyPair(): Promise<CryptoKeyPair | undefined> {
  return (await withStore<CryptoKeyPair | undefined>('readonly', (store) => store.get(KEY_ID))) ?? undefined;
}

/** A fresh key pair, replacing any earlier one (a new activation). */
export async function createKeyPair(): Promise<CryptoKeyPair> {
  const pair = await crypto.subtle.generateKey(ALGORITHM, false, ['sign', 'verify']);
  await withStore('readwrite', (store) => store.put(pair, KEY_ID));
  return pair;
}

export async function forgetKeyPair(): Promise<void> {
  await withStore('readwrite', (store) => store.delete(KEY_ID));
}

/** The public key as X.509 SubjectPublicKeyInfo, base64: what the API's activation expects. */
export async function exportPublicKey(pair: CryptoKeyPair): Promise<string> {
  return toBase64(new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey)));
}

/** An ECDSA P-256 / SHA-256 signature in DER form, base64 (the API verifies with Java's SHA256withECDSA). */
export async function sign(pair: CryptoKeyPair, message: string): Promise<string> {
  const raw = new Uint8Array(
    await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, pair.privateKey, new TextEncoder().encode(message)),
  );
  return toBase64(rawToDer(raw));
}

/**
 * WebCrypto returns r‖s (32 bytes each); DER is SEQUENCE { INTEGER r, INTEGER s }, each integer
 * minimal and positive (a leading 0x00 when the high bit is set).
 */
export function rawToDer(raw: Uint8Array): Uint8Array {
  if (raw.length !== 64) throw new Error('An ECDSA P-256 signature is 64 bytes');
  const integer = (bytes: Uint8Array): number[] => {
    let start = 0;
    while (start < bytes.length - 1 && bytes[start] === 0) start++;
    const trimmed = Array.from(bytes.subarray(start));
    return [0x02, ...(trimmed[0]! & 0x80 ? [trimmed.length + 1, 0x00] : [trimmed.length]), ...trimmed];
  };
  const r = integer(raw.subarray(0, 32));
  const s = integer(raw.subarray(32));
  return Uint8Array.from([0x30, r.length + s.length, ...r, ...s]);
}

function toBase64(bytes: Uint8Array): string {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}
