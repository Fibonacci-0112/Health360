import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    testTimeout: 90_000,
    hookTimeout: 90_000,
    // The clients share one backend; run files one at a time.
    fileParallelism: false,
  },
});
