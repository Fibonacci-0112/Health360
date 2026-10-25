import fc from 'fast-check';
import { describe, expect, it } from 'vitest';
import { uuidv7, uuidv7Time } from './uuidv7.ts';

// Test randomness only; the app passes a cryptographically secure source.
const rng = (n: number) => Uint8Array.from({ length: n }, () => Math.floor(Math.random() * 256));
const V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('uuidv7', () => {
  it('has the v7 version and RFC 9562 variant, and encodes its timestamp', () => {
    fc.assert(
      fc.property(fc.integer({ min: 0, max: 2 ** 48 - 1 }), (ms) => {
        const id = uuidv7(rng, ms);
        return V7.test(id) && uuidv7Time(id) === ms;
      }),
    );
  });

  it('sorts by creation time', () => {
    const a = uuidv7(rng, 1_761_350_400_000);
    const b = uuidv7(rng, 1_761_350_400_001);
    expect(a < b).toBe(true);
  });

  it('matches a fixed vector', () => {
    expect(uuidv7(() => new Uint8Array(10), 0x017f22e279b0)).toBe('017f22e2-79b0-7000-8000-000000000000');
  });
});
