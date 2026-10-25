// Metro config: Expo's monorepo-aware defaults, plus PowerSync's web/native split
// (https://docs.powersync.com/client-sdks/frameworks/react-native-web-support).
const { getDefaultConfig } = require('expo/metro-config');

const config = getDefaultConfig(__dirname);

// The PowerSync web SDK resolves with a React Native-specific export condition.
config.resolver.unstable_conditionsByPlatform.web.push('react-native-web');

const defaultResolve = config.resolver.resolveRequest;
config.resolver.resolveRequest = (context, moduleName, platform) => {
  // Each platform gets only its own PowerSync SDK in the bundle.
  if (platform === 'web' && ['@powersync/react-native', '@powersync/op-sqlite', '@op-engineering/op-sqlite'].includes(moduleName)) {
    return { type: 'empty' };
  }
  if (platform !== 'web' && moduleName === '@powersync/web') {
    return { type: 'empty' };
  }
  return (defaultResolve ?? context.resolveRequest)(context, moduleName, platform);
};

module.exports = config;
