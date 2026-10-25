import { convert, type UnitsByDimension } from './convert.ts';

/**
 * What a quantity column measures, and the canonical unit its `*_canonical` column is stored in
 * (PLAN.md §2.3, §2.6). The canonical value drives math, trends, PRs, charts and AI prompts.
 */
export const QUANTITY_KINDS = {
  body_weight: { dimension: 'mass', canonical: 'kg' },
  load: { dimension: 'mass', canonical: 'kg' },
  food_mass: { dimension: 'mass', canonical: 'g' },
  height: { dimension: 'length', canonical: 'cm' },
  circumference: { dimension: 'length', canonical: 'cm' },
  distance: { dimension: 'length', canonical: 'm' },
  volume: { dimension: 'volume', canonical: 'ml' },
  energy: { dimension: 'energy', canonical: 'kcal' },
  temperature: { dimension: 'temperature', canonical: 'C' },
  glucose: { dimension: 'glucose', canonical: 'mmol/L' },
} as const;

export type QuantityKind = keyof typeof QUANTITY_KINDS;
export type DimensionOf<K extends QuantityKind> = (typeof QUANTITY_KINDS)[K]['dimension'];
export type UnitFor<K extends QuantityKind> = UnitsByDimension[DimensionOf<K>];

/** A stored quantity: the value exactly as typed, its unit, and the canonical value for math. */
export interface Quantity<K extends QuantityKind = QuantityKind> {
  readonly kind: K;
  readonly value: number;
  readonly unit: UnitFor<K>;
  readonly canonical: number;
}

export function canonicalUnit<K extends QuantityKind>(kind: K): UnitFor<K> {
  return QUANTITY_KINDS[kind].canonical as UnitFor<K>;
}

export function toCanonical<K extends QuantityKind>(kind: K, value: number, unit: UnitFor<K>): number {
  return convert(value, unit, canonicalUnit(kind));
}

/** Builds the (value, unit, canonical) trio written to the database. */
export function quantity<K extends QuantityKind>(kind: K, value: number, unit: UnitFor<K>): Quantity<K> {
  if (!Number.isFinite(value)) throw new RangeError(`Quantity value must be finite, got ${value}`);
  return { kind, value, unit, canonical: toCanonical(kind, value, unit) };
}

/**
 * The number to show in `unit`. Shown in the unit it was typed in, the original value comes back
 * untouched, so 225 lb never turns into 224.99 lb. Otherwise it is converted from the canonical value.
 */
export function displayValue<K extends QuantityKind>(q: Quantity<K>, unit: UnitFor<K>): number {
  if (unit === q.unit) return q.value;
  return convert(q.canonical, canonicalUnit(q.kind), unit);
}

/**
 * Compares two canonical values with a relative tolerance, so a PR logged in lb and repeated in
 * kg (or the reverse) is never counted as a new record just from conversion rounding.
 */
export function canonicalEquals(a: number, b: number, relTol = 1e-9): boolean {
  return Math.abs(a - b) <= relTol * Math.max(Math.abs(a), Math.abs(b), 1);
}

export function canonicalGreater(a: number, b: number, relTol = 1e-9): boolean {
  return a > b && !canonicalEquals(a, b, relTol);
}
