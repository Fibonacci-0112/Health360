/**
 * UUIDv7 (RFC 9562): a 48-bit Unix-millisecond timestamp, then random bits. Ids are generated on
 * the client so offline rows have their final id, and v7 keeps them roughly time-ordered in indexes.
 * Randomness is passed in so this stays pure; each platform supplies its own secure source.
 */
export function uuidv7(randomBytes: (n: number) => Uint8Array, nowMs: number = Date.now()): string {
  if (!Number.isSafeInteger(nowMs) || nowMs < 0 || nowMs >= 2 ** 48) throw new RangeError('Timestamp out of range');
  const bytes = new Uint8Array(16);
  bytes.set(randomBytes(10).subarray(0, 10), 6);
  let ms = nowMs;
  for (let i = 5; i >= 0; i--) {
    bytes[i] = ms % 256;
    ms = Math.floor(ms / 256);
  }
  bytes[6] = (bytes[6]! & 0x0f) | 0x70; // version 7
  bytes[8] = (bytes[8]! & 0x3f) | 0x80; // RFC 9562 variant
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** The creation time encoded in a UUIDv7, in Unix milliseconds. */
export function uuidv7Time(id: string): number {
  return parseInt(id.replace(/-/g, '').slice(0, 12), 16);
}
