import base from '@health360/config/eslint';
import globals from 'globals';

export default [
  ...base,
  { ignores: ['public/**', '.expo/**', 'dist/**', 'expo-env.d.ts'] },
  { files: ['metro.config.js'], languageOptions: { sourceType: 'commonjs', globals: globals.node }, rules: { '@typescript-eslint/no-require-imports': 'off' } },
  { files: ['**/*.{ts,tsx}'], languageOptions: { globals: { ...globals.browser } } },
];
