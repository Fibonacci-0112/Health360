import { StyleSheet, Text, View } from 'react-native';
import { colors, space } from './theme';

/** Placeholder for a tab whose module lands in Phase 1. */
export function ComingSoon({ title, section }: { title: string; section: string }) {
  return (
    <View style={styles.center}>
      <Text style={styles.title} role="heading">
        {title}
      </Text>
      <Text style={styles.muted}>Arrives in Phase 1 (FEATURES.md §{section}).</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: space.sm, padding: space.xl },
  title: { fontSize: 22, fontWeight: '700', color: colors.text },
  muted: { color: colors.muted, textAlign: 'center' },
});
