import { UNIT_PREF_OPTIONS, proteinRatioDefault, type UnitPrefs } from '@health360/domain';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { colors, space } from './theme';

const LABELS: { [K in keyof UnitPrefs]: string } = {
  bodyWeight: 'Body weight',
  load: 'Lifting load',
  height: 'Height',
  circumference: 'Circumference',
  distance: 'Distance',
  shortDistance: 'Track and swim',
  pace: 'Pace',
  foodMass: 'Food mass',
  volume: 'Volume',
  energy: 'Energy',
  temperature: 'Temperature',
  glucose: 'Blood glucose',
  labValues: 'Lab values',
  proteinRatio: 'Protein target',
};

const OPTION_LABELS: Record<string, string> = {
  st_lb: 'st + lb',
  ft_in: 'ft + in',
  min_per_km: 'min/km',
  min_per_mi: 'min/mi',
  fl_oz: 'fl oz',
  cup: 'cups',
  C: '°C',
  F: '°F',
  conventional: 'US conventional',
  si: 'SI',
  g_per_kg: 'g/kg',
  g_per_lb: 'g/lb',
};

/** One row per measurement type (PLAN.md §2.6); each can be metric or imperial on its own. */
export function UnitPrefsEditor({ value, onChange }: { value: UnitPrefs; onChange: (next: UnitPrefs) => void }) {
  const set = <K extends keyof UnitPrefs>(key: K, option: UnitPrefs[K]) => {
    const next = { ...value, [key]: option };
    // The protein ratio follows the body-weight unit while it is still on the matching default.
    if (key === 'bodyWeight' && value.proteinRatio === proteinRatioDefault(value.bodyWeight)) {
      next.proteinRatio = proteinRatioDefault(option as UnitPrefs['bodyWeight']);
    }
    onChange(next);
  };

  return (
    <View style={styles.list}>
      {(Object.keys(LABELS) as (keyof UnitPrefs)[]).map((key) => (
        <View key={key} style={styles.row}>
          <Text style={styles.label} nativeID={`unit-${key}`}>
            {LABELS[key]}
          </Text>
          <View style={styles.options} role="radiogroup" aria-labelledby={`unit-${key}`}>
            {(UNIT_PREF_OPTIONS[key] as readonly string[]).map((option) => {
              const selected = value[key] === option;
              return (
                <Pressable
                  key={option}
                  role="radio"
                  aria-checked={selected}
                  onPress={() => set(key, option as UnitPrefs[typeof key])}
                  style={[styles.option, selected && styles.selected]}
                >
                  <Text style={[styles.optionText, selected && styles.selectedText]}>{OPTION_LABELS[option] ?? option}</Text>
                </Pressable>
              );
            })}
          </View>
        </View>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  list: { gap: space.md },
  row: { gap: space.xs },
  label: { color: colors.text, fontWeight: '600' },
  options: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm },
  option: {
    paddingHorizontal: space.md,
    paddingVertical: space.sm,
    borderRadius: 6,
    borderWidth: 1,
    borderColor: colors.border,
    minHeight: 36,
    justifyContent: 'center',
  },
  selected: { backgroundColor: colors.primary, borderColor: colors.primary },
  optionText: { color: colors.text },
  selectedText: { color: colors.white, fontWeight: '600' },
});
