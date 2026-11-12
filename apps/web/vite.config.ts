import { fileURLToPath, URL } from 'node:url';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// In development the browser calls /api on the web origin and Vite forwards it to the local API
// (apps/api, `./mvnw spring-boot:test-run`), so no cross-origin setup is needed. Only used when the
// app runs with VITE_API_MODE=http; the default mock mode never calls /api.
const apiProxy = {
  '/api': { target: process.env.ZAWED_API_URL ?? 'http://localhost:8080', changeOrigin: true },
};

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: { port: 5173, proxy: apiProxy },
  preview: { proxy: apiProxy },
});
