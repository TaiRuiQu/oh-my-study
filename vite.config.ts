import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  clearScreen: false,
  optimizeDeps: { entries: ['index.html'] },
  server: {
    port: 1420,
    strictPort: true,
    watch: {
      ignored: [
        '**/src-tauri/**',
        '**/target/**',
        '**/tmp/**',
        '**/runtime/**',
        '**/output/**',
        '**/probes/**',
      ],
    },
  },
  build: { target: 'chrome105' },
});
