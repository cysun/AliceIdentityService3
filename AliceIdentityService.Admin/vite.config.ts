import { readFileSync } from 'node:fs';
import { fileURLToPath, URL } from 'node:url';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vitest/config';

export default defineConfig(({ command, mode }) => ({
  base: '/admin/',
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    host: 'localhost',
    port: 7443,
    strictPort: true,
    https:
      command === 'serve' && mode !== 'test'
        ? {
            key: readFileSync(new URL('../.dev/certs/localhost.key', import.meta.url)),
            cert: readFileSync(new URL('../.dev/certs/localhost.pem', import.meta.url)),
          }
        : undefined,
    proxy: {
      '^/(Account(?:/|$)|api(?:/|$)|connect(?:/|$)|health(?:/|$|\\?)|css/|js/|brand/|$)': {
        target: 'http://127.0.0.1:5442',
      },
    },
  },
  build: { outDir: '../AliceIdentityService/wwwroot/admin', emptyOutDir: true },
  test: { environment: 'jsdom', setupFiles: ['./src/test/setup.ts'], css: false },
}));
