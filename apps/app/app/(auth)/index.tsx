import { useState } from 'react';
import { KeyboardAvoidingView, Platform, StyleSheet, Text, TextInput, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { z } from 'zod';
import { Button } from '@/components/Button';
import { colors, space } from '@/components/theme';
import { supabase } from '@/lib/supabase';

const emailSchema = z.email();
const codeSchema = z.string().regex(/^\d{6}$/);

/** Sign-in by emailed 6-digit code (PLAN.md §2.1). The same flow creates an account the first time. */
export default function SignIn() {
  const [email, setEmail] = useState('');
  const [code, setCode] = useState('');
  const [step, setStep] = useState<'email' | 'code'>('email');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const sendCode = async () => {
    const parsed = emailSchema.safeParse(email.trim());
    if (!parsed.success) return setError('Enter a valid email address.');
    setBusy(true);
    setError(null);
    const { error } = await supabase.auth.signInWithOtp({ email: parsed.data, options: { shouldCreateUser: true } });
    setBusy(false);
    if (error) return setError(error.message);
    setStep('code');
  };

  const verify = async () => {
    if (!codeSchema.safeParse(code.trim()).success) return setError('The code is 6 digits.');
    setBusy(true);
    setError(null);
    const { error } = await supabase.auth.verifyOtp({ email: email.trim(), token: code.trim(), type: 'email' });
    setBusy(false);
    // On success the auth listener switches screens; nothing else to do here.
    if (error) setError(error.message);
  };

  return (
    <SafeAreaView style={styles.safe}>
      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={styles.body}>
        <Text style={styles.title} role="heading">
          Health360
        </Text>
        {step === 'email' ? (
          <View style={styles.form}>
            <Text style={styles.label} nativeID="email-label">
              Email
            </Text>
            <TextInput
              aria-labelledby="email-label"
              value={email}
              onChangeText={setEmail}
              autoCapitalize="none"
              autoComplete="email"
              keyboardType="email-address"
              inputMode="email"
              placeholder="you@example.com"
              style={styles.input}
              onSubmitEditing={sendCode}
            />
            <Button title="Email me a code" onPress={sendCode} busy={busy} />
          </View>
        ) : (
          <View style={styles.form}>
            <Text style={styles.label} nativeID="code-label">
              Enter the 6-digit code sent to {email.trim()}
            </Text>
            <TextInput
              aria-labelledby="code-label"
              value={code}
              onChangeText={setCode}
              keyboardType="number-pad"
              inputMode="numeric"
              autoComplete="one-time-code"
              textContentType="oneTimeCode"
              maxLength={6}
              style={[styles.input, styles.code]}
              onSubmitEditing={verify}
            />
            <Button title="Sign in" onPress={verify} busy={busy} />
            <Button title="Use a different email" variant="secondary" onPress={() => setStep('email')} />
          </View>
        )}
        {error ? (
          <Text style={styles.error} aria-live="assertive">
            {error}
          </Text>
        ) : null}
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: colors.white },
  body: { flex: 1, padding: space.xl, justifyContent: 'center', gap: space.xl, maxWidth: 480, width: '100%', alignSelf: 'center' },
  title: { fontSize: 32, fontWeight: '700', color: colors.primary },
  form: { gap: space.md },
  label: { color: colors.text, fontWeight: '600' },
  input: { borderWidth: 1, borderColor: colors.border, borderRadius: 8, padding: space.md, fontSize: 16, color: colors.text },
  code: { fontSize: 24, letterSpacing: 8, textAlign: 'center' },
  error: { color: colors.danger },
});
