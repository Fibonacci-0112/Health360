import { M_PER_MI } from './factors.ts';

export type PaceUnit = 'min_per_km' | 'min_per_mi';

/** Pace is stored canonically as seconds per kilometre. */
export function paceToSecondsPerKm(secondsPerUnit: number, unit: PaceUnit): number {
  return unit === 'min_per_km' ? secondsPerUnit : (secondsPerUnit * 1000) / M_PER_MI;
}

export function paceFromSecondsPerKm(secondsPerKm: number, unit: PaceUnit): number {
  return unit === 'min_per_km' ? secondsPerKm : (secondsPerKm * M_PER_MI) / 1000;
}

/** Seconds per km from a distance (m) and a duration (s). */
export function paceFromDistance(distanceM: number, durationS: number): number {
  if (distanceM <= 0) throw new RangeError('Distance must be positive');
  return (durationS * 1000) / distanceM;
}
