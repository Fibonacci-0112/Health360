/**
 * Rounds half away from zero to `decimals` places. The (1 + ε) nudge stops binary
 * representation from rounding halves down (1.005 × 100 is 100.49999999999999).
 */
export function roundTo(value: number, decimals = 0): number {
  if (!Number.isFinite(value)) return value;
  const factor = 10 ** decimals;
  const sign = value < 0 ? -1 : 1;
  return (sign * Math.round(Math.abs(value) * factor * (1 + Number.EPSILON))) / factor;
}
