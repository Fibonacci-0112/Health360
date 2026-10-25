import { defaultUnitPrefs, type UnitPrefs } from '@health360/domain';
import { unitPrefsSchema } from '@health360/schemas';
import { useQuery } from '@powersync/react';
import { getLocales } from 'expo-localization';
import { useAuth } from './auth';

export interface ProfileRow {
  id: string;
  display_name: string | null;
  unit_prefs: string | null;
  onboarded_at: string | null;
  height: number | null;
  height_unit: string | null;
}

/** The signed-in user's own profile, from the local database. Undefined until it has synced. */
export function useMyProfile() {
  const { session } = useAuth();
  const { data, isLoading } = useQuery<ProfileRow>(
    'SELECT id, display_name, unit_prefs, onboarded_at, height, height_unit FROM profiles WHERE user_id = ?',
    [session?.user.id ?? ''],
  );
  return { profile: data[0], isLoading };
}

export function deviceLocale(): string {
  return getLocales()[0]?.languageTag ?? 'en-US';
}

/** Stored preferences, or the locale defaults when none are saved (or they don't parse). */
export function parseUnitPrefs(json: string | null | undefined): UnitPrefs {
  const fallback = defaultUnitPrefs(deviceLocale());
  if (!json) return fallback;
  try {
    const parsed = unitPrefsSchema.safeParse({ ...fallback, ...JSON.parse(json) });
    return parsed.success ? parsed.data : fallback;
  } catch {
    return fallback;
  }
}
