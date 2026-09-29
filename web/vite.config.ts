import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'

// Backend integration (https://vite.dev/guide/backend-integration): the HTML shell is rendered by the
// agentd Host (Razor). In production the Host reads .vite/manifest.json; in development it proxies the
// Vite paths (/@vite, /src, /__vite_hmr, …) to this dev server, so the browser stays on the Host origin.
export default defineConfig({
  plugins: [vue(), tailwindcss()],
  server: {
    // HMR websocket goes through the Host proxy on a path that can't collide with app routes.
    hmr: { path: '/__vite_hmr' },
  },
  build: {
    manifest: true, // → wwwroot/.vite/manifest.json
    outDir: '../src/Agentd.Host/wwwroot',
    emptyOutDir: true,
    rollupOptions: { input: 'src/main.ts' },
  },
  test: {
    environment: 'jsdom',
    include: ['tests/**/*.spec.ts'],
  },
})
