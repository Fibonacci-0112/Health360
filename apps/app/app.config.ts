import type { ExpoConfig } from 'expo/config';

const config: ExpoConfig = {
  name: 'Health360',
  slug: 'health360',
  scheme: 'health360',
  version: '0.0.0',
  orientation: 'portrait',
  userInterfaceStyle: 'automatic',
  platforms: ['android', 'ios', 'web'],
  android: {
    package: 'app.health360',
  },
  // Apple is deferred (PLAN.md §2.7); this keeps the monthly EAS iOS simulator build working.
  ios: {
    bundleIdentifier: 'app.health360',
    supportsTablet: true,
  },
  web: {
    bundler: 'metro',
    // A single index.html: what Tauri wraps, and what the PWA serves for every deep link.
    output: 'single',
    name: 'Health360',
    shortName: 'Health360',
    themeColor: '#0f766e',
    backgroundColor: '#ffffff',
  },
  plugins: ['expo-router', 'expo-localization', 'expo-splash-screen'],
  experiments: {
    typedRoutes: true,
  },
};

export default config;
