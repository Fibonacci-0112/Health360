import { ActivityIndicator, Pressable, StyleSheet, Text } from 'react-native';
import { colors, space } from './theme';

interface Props {
  title: string;
  onPress: () => void;
  disabled?: boolean;
  busy?: boolean;
  variant?: 'primary' | 'secondary' | 'danger';
}

export function Button({ title, onPress, disabled, busy, variant = 'primary' }: Props) {
  const inactive = disabled || busy;
  return (
    <Pressable
      role="button"
      aria-disabled={!!inactive}
      aria-busy={!!busy}
      onPress={onPress}
      disabled={inactive}
      style={({ pressed }) => [styles.base, styles[variant], (pressed || inactive) && styles.dim]}
    >
      {busy ? (
        <ActivityIndicator color={variant === 'secondary' ? colors.primary : colors.white} />
      ) : (
        <Text style={[styles.label, variant === 'secondary' && styles.secondaryLabel]}>{title}</Text>
      )}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  base: { paddingVertical: space.md, paddingHorizontal: space.lg, borderRadius: 8, alignItems: 'center', minHeight: 44 },
  primary: { backgroundColor: colors.primary },
  secondary: { backgroundColor: colors.white, borderWidth: 1, borderColor: colors.primary },
  danger: { backgroundColor: colors.danger },
  dim: { opacity: 0.6 },
  label: { color: colors.white, fontWeight: '600', fontSize: 16 },
  secondaryLabel: { color: colors.primary },
});
