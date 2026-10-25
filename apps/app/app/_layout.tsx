import { PowerSyncContext, useStatus } from '@powersync/react';
import { Stack } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { useEffect } from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { colors, space } from '@/components/theme';
import { powersync } from '@/db/powersync';
import { AuthProvider, useAuth } from '@/lib/auth';
import { useMyProfile } from '@/lib/profile';
import { registerServiceWorker, requestPersistentStorage } from '@/platform/storage';

export default function RootLayout() {
  useEffect(() => {
    void requestPersistentStorage();
    registerServiceWorker();
  }, []);

  return (
    <SafeAreaProvider>
      <PowerSyncContext.Provider value={powersync}>
        <AuthProvider>
          <Routes />
          <StatusBar style="auto" />
        </AuthProvider>
      </PowerSyncContext.Provider>
    </SafeAreaProvider>
  );
}

function Routes() {
  const { session, loading } = useAuth();
  const { profile, isLoading: profileLoading } = useMyProfile();
  const status = useStatus();

  if (loading) return <Waiting label="Loading" />;
  // Wait for the local profile query before choosing routes, or a deep link would be redirected away
  // while the guards briefly think the user isn't onboarded.
  if (session && profileLoading) return <Waiting label="Loading" />;
  // Signed in but the profile row hasn't arrived yet (first sign-in on this device).
  if (session && !profile && !status.hasSynced) return <Waiting label="Setting up this device" />;

  const signedIn = !!session;
  const onboarded = !!profile?.onboarded_at;

  return (
    <Stack screenOptions={{ headerShown: false }}>
      <Stack.Protected guard={!signedIn}>
        <Stack.Screen name="(auth)" />
      </Stack.Protected>
      <Stack.Protected guard={signedIn && !onboarded}>
        <Stack.Screen name="onboarding" />
      </Stack.Protected>
      <Stack.Protected guard={signedIn && onboarded}>
        <Stack.Screen name="(tabs)" />
        <Stack.Screen name="settings" options={{ headerShown: true, title: 'Settings' }} />
      </Stack.Protected>
    </Stack>
  );
}

function Waiting({ label }: { label: string }) {
  return (
    <View style={styles.center} aria-live="polite">
      <ActivityIndicator color={colors.primary} />
      <Text style={styles.muted}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: space.md },
  muted: { color: colors.muted },
});
