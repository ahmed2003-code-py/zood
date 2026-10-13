import { fileURLToPath, URL } from 'node:url';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
    // tokens.contrast.test.ts reads tokens.css via ?raw; Vitest blanks CSS unless it is included.
    css: { include: [/tokens\.css/] },
  },
});
