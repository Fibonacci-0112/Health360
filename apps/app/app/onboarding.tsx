import { ACTIVITY_LEVELS, SEX_FOR_CALCS, onboardingSchema } from '@health360/schemas';
import { usePowerSync } from '@powersync/react';
import { getCalendars, getLocales } from 'expo-localization';
import { useState } from 'react';
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { Button } from '@/components/Button';
import { colors, space } from '@/components/theme';
import { UnitPrefsEditor } from '@/components/UnitPrefsEditor';
import { parseHeight, type HeightEntry } from '@/features/onboarding/height';
import { parseUnitPrefs, useMyProfile, type ProfileRow } from '@/lib/profile';

const ACTIVITY_LABELS: Record<(typeof ACTIVITY_LEVELS)[number], string> = {
  sedentary: 'Mostly sitting',
  light: 'Light',
  moderate: 'Moderate',
  active: 'Active',
  very_active: 'Very active',
};

/**
 * First-run setup. Writes go to the local database and sync when online, like every other write.
 * Unit defaults come from the device locale and every type can be changed (PLAN.md §2.6).
 */
export default function Onboarding() {
  const { profile } = useMyProfile();
  // The form seeds its state from the profile, so it waits for the local query to return it.
  if (!profile) return <ActivityIndicator style={styles.loading} color={colors.primary} />;
  return <OnboardingForm profile={profile} />;
}

function OnboardingForm({ profile }: { profile: ProfileRow }) {
  const db = usePowerSync();
  const [name, setName] = useState(profile.display_name ?? '');
  const [prefs, setPrefs] = useState(() => parseUnitPrefs(profile.unit_prefs));
  const [height, setHeight] = useState<HeightEntry>({});
  const [birthDate, setBirthDate] = useState('');
  const [sex, setSex] = useState<(typeof SEX_FOR_CALCS)[number] | null>(null);
  const [activity, setActivity] = useState<(typeof ACTIVITY_LEVELS)[number] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setError(null);
    let h: ReturnType<typeof parseHeight>;
    try {
      h = parseHeight(height, prefs.height);
    } catch (e) {
      return setError((e as Error).message);
    }
    const parsed = onboardingSchema.safeParse({
      display_name: name,
      birth_date: birthDate.trim() || null,
      sex_for_calcs: sex,
      height: h?.height ?? null,
      height_unit: h?.height_unit ?? null,
      timezone: getCalendars()[0]?.timeZone ?? 'UTC',
      currency: getLocales()[0]?.currencyCode ?? 'USD',
      activity_level: activity,
      unit_prefs: prefs,
    });
    if (!parsed.success) {
      const field = parsed.error.issues[0]?.path[0];
      return setError(field === 'birth_date' ? 'Birth date must look like 1990-04-02.' : 'Please check your details.');
    }
    const p = parsed.data;
    setBusy(true);
    await db.execute(
      `UPDATE profiles SET display_name = ?, birth_date = ?, sex_for_calcs = ?, height = ?, height_unit = ?,
         height_cm = ?, timezone = ?, currency = ?, activity_level = ?, unit_prefs = ?, onboarded_at = ?
       WHERE id = ?`,
      [
        p.display_name,
        p.birth_date,
        p.sex_for_calcs,
        p.height,
        p.height_unit,
        h?.height_cm ?? null,
        p.timezone,
        p.currency,
        p.activity_level,
        JSON.stringify(p.unit_prefs),
        new Date().toISOString(),
        profile.id,
      ],
    );
    setBusy(false);
  };

  return (
    <SafeAreaView style={styles.safe}>
      <ScrollView contentContainerStyle={styles.body} keyboardShouldPersistTaps="handled">
        <Text style={styles.title} role="heading">
          Set up your profile
        </Text>
        <Text style={styles.muted}>
          Health360 is a wellness app, not a medical device. Calculations need only a few details, and you can change
          everything later.
        </Text>

        <Field label="Name" id="name">
          <TextInput aria-labelledby="name" value={name} onChangeText={setName} style={styles.input} maxLength={80} />
        </Field>

        <Text style={styles.section} role="heading">
          Units
        </Text>
        <UnitPrefsEditor value={prefs} onChange={setPrefs} />

        <Text style={styles.section} role="heading">
          About you (optional)
        </Text>
        <Field label={prefs.height === 'cm' ? 'Height (cm)' : 'Height (ft, in)'} id="height">
          {prefs.height === 'cm' ? (
            <TextInput
              aria-labelledby="height"
              inputMode="decimal"
              keyboardType="decimal-pad"
              value={height.cm ?? ''}
              onChangeText={(cm) => setHeight({ cm })}
              style={styles.input}
            />
          ) : (
            <View style={styles.inline}>
              <TextInput
                aria-label="Feet"
                inputMode="numeric"
                keyboardType="number-pad"
                value={height.ft ?? ''}
                onChangeText={(ft) => setHeight({ ...height, ft })}
                style={[styles.input, styles.short]}
                placeholder="ft"
              />
              <TextInput
                aria-label="Inches"
                inputMode="decimal"
                keyboardType="decimal-pad"
                value={height.in ?? ''}
                onChangeText={(inches) => setHeight({ ...height, in: inches })}
                style={[styles.input, styles.short]}
                placeholder="in"
              />
            </View>
          )}
        </Field>
        <Field label="Birth date (YYYY-MM-DD)" id="birth">
          <TextInput
            aria-labelledby="birth"
            value={birthDate}
            onChangeText={setBirthDate}
            placeholder="1990-04-02"
            style={styles.input}
          />
        </Field>
        <Field label="Sex used for calculations" id="sex">
          <Choices options={SEX_FOR_CALCS} value={sex} onChange={setSex} labels={{ female: 'Female', male: 'Male' }} />
        </Field>
        <Field label="Activity level" id="activity">
          <Choices options={ACTIVITY_LEVELS} value={activity} onChange={setActivity} labels={ACTIVITY_LABELS} />
        </Field>

        {error ? (
          <Text style={styles.error} aria-live="assertive">
            {error}
          </Text>
        ) : null}
        <Button title="Continue" onPress={save} busy={busy} disabled={!name.trim()} />
      </ScrollView>
    </SafeAreaView>
  );
}

function Field({ label, id, children }: { label: string; id: string; children: React.ReactNode }) {
  return (
    <View style={styles.field}>
      <Text style={styles.label} nativeID={id}>
        {label}
      </Text>
      {children}
    </View>
  );
}

function Choices<T extends string>(props: {
  options: readonly T[];
  value: T | null;
  onChange: (v: T | null) => void;
  labels: Record<T, string>;
}) {
  return (
    <View style={styles.inline} role="radiogroup">
      {props.options.map((o) => {
        const selected = props.value === o;
        return (
          <Pressable
            key={o}
            role="radio"
            aria-checked={selected}
            onPress={() => props.onChange(selected ? null : o)}
            style={[styles.chip, selected && styles.chipOn]}
          >
            <Text style={selected ? styles.chipOnText : styles.chipText}>{props.labels[o]}</Text>
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: colors.white },
  loading: { flex: 1 },
  body: { padding: space.xl, gap: space.lg, maxWidth: 640, width: '100%', alignSelf: 'center' },
  title: { fontSize: 26, fontWeight: '700', color: colors.text },
  section: { fontSize: 18, fontWeight: '700', color: colors.text, marginTop: space.md },
  muted: { color: colors.muted },
  field: { gap: space.xs },
  label: { color: colors.text, fontWeight: '600' },
  input: { borderWidth: 1, borderColor: colors.border, borderRadius: 8, padding: space.md, fontSize: 16, color: colors.text },
  inline: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm },
  short: { width: 96 },
  chip: { paddingHorizontal: space.md, paddingVertical: space.sm, borderRadius: 6, borderWidth: 1, borderColor: colors.border, minHeight: 36, justifyContent: 'center' },
  chipOn: { backgroundColor: colors.primary, borderColor: colors.primary },
  chipText: { color: colors.text },
  chipOnText: { color: colors.white, fontWeight: '600' },
  error: { color: colors.danger },
});
