import { ENERGY, GLUCOSE, LENGTH, MASS, VOLUME, type Ratio } from './factors.ts';

export type MassUnit = keyof typeof MASS;
export type LengthUnit = keyof typeof LENGTH;
export type VolumeUnit = keyof typeof VOLUME;
export type EnergyUnit = keyof typeof ENERGY;
export type GlucoseUnit = keyof typeof GLUCOSE;
export type TemperatureUnit = 'C' | 'F';

export interface UnitsByDimension {
  mass: MassUnit;
  length: LengthUnit;
  volume: VolumeUnit;
  energy: EnergyUnit;
  glucose: GlucoseUnit;
  temperature: TemperatureUnit;
}
export type Dimension = keyof UnitsByDimension;
export type Unit = UnitsByDimension[Dimension];

const RATIO_TABLES = {
  mass: MASS,
  length: LENGTH,
  volume: VOLUME,
  energy: ENERGY,
  glucose: GLUCOSE,
} as const satisfies Record<Exclude<Dimension, 'temperature'>, Record<string, Ratio>>;

const TEMPERATURE_UNITS: readonly TemperatureUnit[] = ['C', 'F'];

export const UNITS: { readonly [D in Dimension]: readonly UnitsByDimension[D][] } = {
  mass: Object.keys(MASS) as MassUnit[],
  length: Object.keys(LENGTH) as LengthUnit[],
  volume: Object.keys(VOLUME) as VolumeUnit[],
  energy: Object.keys(ENERGY) as EnergyUnit[],
  glucose: Object.keys(GLUCOSE) as GlucoseUnit[],
  temperature: TEMPERATURE_UNITS,
};

export function dimensionOf(unit: Unit): Dimension {
  for (const dim of Object.keys(UNITS) as Dimension[]) {
    if ((UNITS[dim] as readonly string[]).includes(unit)) return dim;
  }
  throw new Error(`Unknown unit: ${unit}`);
}

export function isUnitOf<D extends Dimension>(dim: D, unit: string): unit is UnitsByDimension[D] {
  return (UNITS[dim] as readonly string[]).includes(unit);
}

/** Converts between two units of the same dimension. Throws on a dimension mismatch. */
export function convert<D extends Dimension>(
  value: number,
  from: UnitsByDimension[D],
  to: UnitsByDimension[D],
): number {
  if (from === to) return value;
  const dim = dimensionOf(from);
  if (dimensionOf(to) !== dim) throw new Error(`Cannot convert ${from} to ${to}`);
  if (dim === 'temperature') {
    return from === 'C' ? (value * 9) / 5 + 32 : ((value - 32) * 5) / 9;
  }
  const table = RATIO_TABLES[dim] as Record<string, Ratio>;
  const a = table[from]!;
  const b = table[to]!;
  // value × a.num/a.den gives the base unit; × b.den/b.num leaves it in `to`.
  // Multiplying both numerators first keeps exact pairs (e.g. lb↔kg) to one rounding step.
  return (value * a.num * b.den) / (a.den * b.num);
}
