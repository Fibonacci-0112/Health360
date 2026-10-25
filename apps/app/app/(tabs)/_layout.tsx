import { Link, Tabs } from 'expo-router';
import { Text } from 'react-native';
import { colors } from '@/components/theme';

function SettingsLink() {
  return (
    <Link href="/settings" aria-label="Settings" style={{ color: colors.primary, marginHorizontal: 16, fontWeight: '600' }}>
      Settings
    </Link>
  );
}

/** Today, Food, Train, Body and Plan (PLAN.md §2.2). Only Today does anything in Phase 0. */
export default function TabsLayout() {
  return (
    <Tabs screenOptions={{ tabBarActiveTintColor: colors.primary, headerRight: SettingsLink, tabBarIcon: () => <Text> </Text> }}>
      <Tabs.Screen name="index" options={{ title: 'Today' }} />
      <Tabs.Screen name="food" options={{ title: 'Food' }} />
      <Tabs.Screen name="train" options={{ title: 'Train' }} />
      <Tabs.Screen name="body" options={{ title: 'Body' }} />
      <Tabs.Screen name="plan" options={{ title: 'Plan' }} />
    </Tabs>
  );
}
