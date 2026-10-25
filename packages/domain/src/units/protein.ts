import { KG_PER_LB } from './factors.ts';

export type ProteinRatioUnit = 'g_per_kg' | 'g_per_lb';

/** The canonical protein ratio is g per kg (of body weight or of lean mass). */
export function proteinRatioToGPerKg(ratio: number, unit: ProteinRatioUnit): number {
  // 1 g/lb = 1 g per 0.45359237 kg.
  return unit === 'g_per_kg' ? ratio : ratio / KG_PER_LB;
}

export function proteinRatioFromGPerKg(gPerKg: number, unit: ProteinRatioUnit): number {
  return unit === 'g_per_kg' ? gPerKg : gPerKg * KG_PER_LB;
}

/** Daily protein grams from a ratio and the reference mass in kg (body weight or lean mass). */
export function dailyProteinGrams(ratio: number, unit: ProteinRatioUnit, referenceMassKg: number): number {
  return proteinRatioToGPerKg(ratio, unit) * referenceMassKg;
}
