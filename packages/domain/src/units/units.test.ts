import fc from 'fast-check';
import { describe, expect, it } from 'vitest';
import {
  UNITS,
  QUANTITY_KINDS,
  canonicalEquals,
  canonicalGreater,
  cmToFeetInches,
  convert,
  dailyProteinGrams,
  defaultUnitPrefs,
  displayValue,
  feetInchesToCm,
  kgToStoneLb,
  paceFromSecondsPerKm,
  paceToSecondsPerKm,
  proteinRatioFromGPerKg,
  proteinRatioToGPerKg,
  quantity,
  regionOf,
  roundTo,
  stoneLbToKg,
  type Dimension,
  type QuantityKind,
  type Unit,
  type UnitFor,
} from './index.ts';

const close = (a: number, b: number, rel = 1e-12) =>
  Math.abs(a - b) <= rel * Math.max(Math.abs(a), Math.abs(b), 1);

const magnitude = fc.double({ min: 0, max: 1e6, noNaN: true, noDefaultInfinity: true });
const signed = fc.double({ min: -1e6, max: 1e6, noNaN: true, noDefaultInfinity: true });

const unitPair = (dim: Dimension) =>
  fc.tuple(fc.constantFrom(...(UNITS[dim] as readonly Unit[])), fc.constantFrom(...(UNITS[dim] as readonly Unit[])));

describe('exact factors', () => {
  it.each([
    [1, 'lb', 'kg', 0.45359237],
    [1, 'in', 'cm', 2.54],
    [1, 'mi', 'm', 1609.344],
    [1, 'oz', 'g', 28.349523125],
    [1, 'fl_oz', 'ml', 29.5735295625],
    [1, 'kcal', 'kJ', 4.184],
    [1, 'ft', 'cm', 30.48],
    [1, 'yd', 'm', 0.9144],
    [1, 'cup', 'ml', 236.5882365],
    [100, 'mg/dL', 'mmol/L', 100 / 18.0156],
    [100, 'C', 'F', 212],
    [-40, 'F', 'C', -40],
  ] as const)('%d %s = %d %s', (v, from, to, expected) => {
    expect(close(convert(v, from as Unit as never, to as Unit as never), expected)).toBe(true);
  });

  it('rejects a dimension mismatch', () => {
    expect(() => convert(1, 'kg' as never, 'cm' as never)).toThrow();
  });
});

describe('round-trips', () => {
  for (const dim of Object.keys(UNITS) as Dimension[]) {
    it(`every ${dim} unit pair round-trips`, () => {
      fc.assert(
        fc.property(dim === 'temperature' ? signed : magnitude, unitPair(dim), (v, [a, b]) => {
          const back = convert(convert(v, a as never, b as never), b as never, a as never);
          // Temperature is affine, so near 0 the error is absolute rather than relative.
          return dim === 'temperature' ? Math.abs(back - v) < 1e-9 : close(back, v);
        }),
      );
    });
  }
});

describe('stored as entered', () => {
  const kinds = Object.keys(QUANTITY_KINDS) as QuantityKind[];

  it('a value shown in the unit it was typed in comes back exactly', () => {
    fc.assert(
      fc.property(
        fc.constantFrom(...kinds).chain((kind) =>
          fc.tuple(
            fc.constant(kind),
            fc.constantFrom(...(UNITS[QUANTITY_KINDS[kind].dimension] as readonly Unit[])),
          ),
        ),
        magnitude,
        ([kind, unit], v) => displayValue(quantity(kind, v, unit as never), unit as never) === v,
      ),
    );
  });

  it('225 lb stays 225 lb, and shows as the right kg', () => {
    const q = quantity('body_weight', 225, 'lb');
    expect(displayValue(q, 'lb')).toBe(225);
    expect(q.canonical).toBeCloseTo(102.0582833, 6);
    expect(displayValue(q, 'kg')).toBe(q.canonical);
  });

  it('canonical units match the column names in the plan', () => {
    expect(quantity('height', 70, 'in').canonical).toBeCloseTo(177.8, 10);
    expect(quantity('distance', 1, 'mi').canonical).toBe(1609.344);
    expect(quantity('food_mass', 1, 'oz').canonical).toBeCloseTo(28.349523125, 12);
    expect(quantity('glucose', 90, 'mg/dL').canonical).toBeCloseTo(4.9957, 4);
  });

  it('rejects non-finite values', () => {
    expect(() => quantity('load', Number.NaN, 'kg')).toThrow(RangeError);
  });
});

describe('switching units never creates a PR', () => {
  it('the same load logged in kg and then in lb is not a new record', () => {
    fc.assert(
      fc.property(fc.double({ min: 0.5, max: 500, noNaN: true }), (kg) => {
        const first = quantity('load', kg, 'kg');
        const lb = convert(kg, 'kg', 'lb');
        const second = quantity('load', lb, 'lb' as UnitFor<'load'>);
        return !canonicalGreater(second.canonical, first.canonical) && canonicalEquals(first.canonical, second.canonical);
      }),
    );
  });

  it('a genuinely heavier lift still counts', () => {
    const prev = quantity('load', 100, 'kg');
    const next = quantity('load', 225, 'lb'); // 102.06 kg
    expect(canonicalGreater(next.canonical, prev.canonical)).toBe(true);
  });
});

describe('compound display', () => {
  it('stone and pounds carry instead of showing 14 lb', () => {
    expect(kgToStoneLb(stoneLbToKg({ st: 12, lb: 13.97 }), 1)).toEqual({ st: 13, lb: 0 });
    expect(kgToStoneLb(stoneLbToKg({ st: 11, lb: 4 }), 0)).toEqual({ st: 11, lb: 4 });
  });

  it('feet and inches carry instead of showing 12 in', () => {
    expect(cmToFeetInches(182.85, 0)).toEqual({ ft: 6, in: 0 });
    expect(cmToFeetInches(feetInchesToCm({ ft: 5, in: 10 }), 0)).toEqual({ ft: 5, in: 10 });
  });

  it('pounds and inches are always within range', () => {
    fc.assert(
      fc.property(fc.double({ min: 0, max: 400, noNaN: true }), fc.integer({ min: 0, max: 2 }), (v, d) => {
        const s = kgToStoneLb(v, d);
        const f = cmToFeetInches(v, d);
        return s.lb >= 0 && s.lb < 14 && f.in >= 0 && f.in < 12;
      }),
    );
  });

  it('round-trips through stone and pounds', () => {
    fc.assert(
      fc.property(fc.integer({ min: 0, max: 50 }), fc.integer({ min: 0, max: 13 }), (st, lb) => {
        return kgToStoneLb(stoneLbToKg({ st, lb }), 0).st === st && kgToStoneLb(stoneLbToKg({ st, lb }), 0).lb === lb;
      }),
    );
  });
});

describe('pace', () => {
  it('5:00 min/km is 8:02.7 min/mi', () => {
    expect(paceFromSecondsPerKm(300, 'min_per_mi')).toBeCloseTo(482.8032, 4);
  });

  it('round-trips', () => {
    fc.assert(
      fc.property(fc.double({ min: 60, max: 3600, noNaN: true }), (s) =>
        close(paceToSecondsPerKm(paceFromSecondsPerKm(s, 'min_per_mi'), 'min_per_mi'), s),
      ),
    );
  });
});

describe('protein ratio', () => {
  it('1 g/lb is 2.2046 g/kg', () => {
    expect(proteinRatioToGPerKg(1, 'g_per_lb')).toBeCloseTo(2.20462262, 8);
  });

  it('round-trips between g/kg and g/lb', () => {
    fc.assert(
      fc.property(fc.double({ min: 0.1, max: 5, noNaN: true }), (r) =>
        close(proteinRatioToGPerKg(proteinRatioFromGPerKg(r, 'g_per_lb'), 'g_per_lb'), r),
      ),
    );
  });

  it('switching the ratio unit keeps the daily gram target within rounding', () => {
    fc.assert(
      fc.property(
        fc.double({ min: 0.8, max: 3.3, noNaN: true }),
        fc.double({ min: 30, max: 200, noNaN: true }),
        (gPerKg, massKg) => {
          // The user sees the converted ratio rounded to 2 decimals, as the UI shows it.
          const shownPerLb = roundTo(proteinRatioFromGPerKg(gPerKg, 'g_per_lb'), 2);
          const before = dailyProteinGrams(gPerKg, 'g_per_kg', massKg);
          const after = dailyProteinGrams(shownPerLb, 'g_per_lb', massKg);
          // ±0.005 g/lb of rounding on up to 200 kg (441 lb) is at most ~2.2 g.
          return Math.abs(after - before) <= 0.005 * (massKg / 0.45359237) + 1e-9;
        },
      ),
    );
  });
});

describe('roundTo', () => {
  it('rounds halves away from zero', () => {
    expect(roundTo(1.005, 2)).toBe(1.01);
    expect(roundTo(-2.5, 0)).toBe(-3);
    expect(roundTo(1e-7, 2)).toBe(0);
  });
});

describe('locale defaults', () => {
  it.each([
    ['en-US', 'lb', 'F', 'mg/dL'],
    ['en-GB', 'st_lb', 'C', 'mmol/L'],
    ['de-DE', 'kg', 'C', 'mmol/L'],
    ['en', 'kg', 'C', 'mmol/L'],
  ] as const)('%s', (locale, bodyWeight, temperature, glucose) => {
    const p = defaultUnitPrefs(locale);
    expect([p.bodyWeight, p.temperature, p.glucose]).toEqual([bodyWeight, temperature, glucose]);
  });

  it('protein ratio follows the body-weight unit', () => {
    expect(defaultUnitPrefs('en-US').proteinRatio).toBe('g_per_lb');
    expect(defaultUnitPrefs('en-GB').proteinRatio).toBe('g_per_lb');
    expect(defaultUnitPrefs('fr-FR').proteinRatio).toBe('g_per_kg');
  });

  it('Australia defaults to kJ', () => {
    expect(defaultUnitPrefs('en-AU').energy).toBe('kJ');
  });

  it('parses region subtags', () => {
    expect(regionOf('de-Latn-AT')).toBe('AT');
    expect(regionOf('en_GB')).toBe('GB');
    expect(regionOf('es-419')).toBe('419');
    expect(regionOf('en')).toBeNull();
  });
});
