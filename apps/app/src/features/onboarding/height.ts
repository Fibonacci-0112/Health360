import { feetInchesToCm, toCanonical, type UnitPrefs } from '@health360/domain';

export interface HeightEntry {
  /** As typed: cm, or feet and inches. */
  cm?: string;
  ft?: string;
  in?: string;
}

/**
 * Turns the height fields into the stored trio (PLAN.md §2.6). Feet and inches are stored as total
 * inches, so the original entry can be shown again exactly. Returns null when nothing was entered,
 * and throws on an implausible value.
 */
export function parseHeight(
  entry: HeightEntry,
  pref: UnitPrefs['height'],
): { height: number; height_unit: 'cm' | 'in'; height_cm: number } | null {
  if (pref === 'cm') {
    if (!entry.cm?.trim()) return null;
    const cm = Number(entry.cm.replace(',', '.'));
    check(cm);
    return { height: cm, height_unit: 'cm', height_cm: cm };
  }
  if (!entry.ft?.trim() && !entry.in?.trim()) return null;
  const ft = Number(entry.ft || 0);
  const inches = Number((entry.in || '0').replace(',', '.'));
  if (!Number.isInteger(ft) || inches < 0 || inches >= 12) throw new RangeError('Use whole feet and 0–11 inches.');
  const cm = feetInchesToCm({ ft, in: inches });
  check(cm);
  const total = ft * 12 + inches;
  return { height: total, height_unit: 'in', height_cm: toCanonical('height', total, 'in') };
}

function check(cm: number) {
  if (!Number.isFinite(cm) || cm < 50 || cm > 272) throw new RangeError('That height looks wrong.');
}
