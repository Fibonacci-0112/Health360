import { defaultUnitPrefs } from '@health360/domain';
import { describe, expect, it } from 'vitest';
import { z } from 'zod';
import { aiHelloOutputSchema, onboardingSchema, unitPrefsSchema } from './index.ts';

describe('unit prefs', () => {
  it('accepts every locale default', () => {
    for (const locale of ['en-US', 'en-GB', 'de-DE', 'en-AU', 'ja-JP']) {
      expect(unitPrefsSchema.parse(defaultUnitPrefs(locale))).toEqual(defaultUnitPrefs(locale));
    }
  });

  it('rejects an unknown unit', () => {
    expect(unitPrefsSchema.safeParse({ ...defaultUnitPrefs('en-US'), load: 'stone' }).success).toBe(false);
  });
});

describe('onboarding', () => {
  it('parses a typical profile', () => {
    const parsed = onboardingSchema.parse({
      display_name: ' Sam ',
      birth_date: '1990-04-02',
      sex_for_calcs: 'female',
      height: 65,
      height_unit: 'in',
      timezone: 'America/Chicago',
      currency: 'USD',
      activity_level: 'moderate',
      unit_prefs: defaultUnitPrefs('en-US'),
    });
    expect(parsed.display_name).toBe('Sam');
  });
});

describe('AI output schema', () => {
  it('converts to a JSON schema with no unsupported constructs', () => {
    const json = z.toJSONSchema(aiHelloOutputSchema);
    expect(json.type).toBe('object');
    expect(json.required).toEqual(['greeting', 'tip']);
  });
});
