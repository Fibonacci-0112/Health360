import { UNIT_PREF_OPTIONS, type UnitPrefs } from '@health360/domain';
import { z } from 'zod';
import { localDate, uuid } from './common.ts';

const options = UNIT_PREF_OPTIONS;
const pick = <K extends keyof UnitPrefs>(key: K) =>
  z.enum(options[key] as unknown as [UnitPrefs[K], ...UnitPrefs[K][]]);

export const unitPrefsSchema = z.object({
  bodyWeight: pick('bodyWeight'),
  load: pick('load'),
  height: pick('height'),
  circumference: pick('circumference'),
  distance: pick('distance'),
  shortDistance: pick('shortDistance'),
  pace: pick('pace'),
  foodMass: pick('foodMass'),
  volume: pick('volume'),
  energy: pick('energy'),
  temperature: pick('temperature'),
  glucose: pick('glucose'),
  labValues: pick('labValues'),
  proteinRatio: pick('proteinRatio'),
}) satisfies z.ZodType<UnitPrefs>;

export const SEX_FOR_CALCS = ['female', 'male'] as const;
export const ACTIVITY_LEVELS = ['sedentary', 'light', 'moderate', 'active', 'very_active'] as const;

/** What onboarding collects. Matches the `profiles` columns it writes. */
export const onboardingSchema = z.object({
  display_name: z.string().trim().min(1).max(80),
  birth_date: localDate.nullable(),
  sex_for_calcs: z.enum(SEX_FOR_CALCS).nullable(),
  height: z.number().positive().max(300).nullable(),
  height_unit: z.enum(['cm', 'in']).nullable(),
  timezone: z.string().min(1),
  currency: z.string().length(3),
  activity_level: z.enum(ACTIVITY_LEVELS).nullable(),
  unit_prefs: unitPrefsSchema,
});
export type Onboarding = z.infer<typeof onboardingSchema>;

export const profileSchema = onboardingSchema.extend({
  id: uuid,
  user_id: uuid.nullable(),
  height_cm: z.number().nullable(),
  onboarded_at: z.string().nullable(),
});
export type Profile = z.infer<typeof profileSchema>;
