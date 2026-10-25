import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

// Plain-TS logic only. Component tests (jest-expo + RNTL) come with the first real screens in Phase 1.
export default defineConfig({
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  test: { include: ['src/**/*.test.ts'] },
});
