import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'

// Host (agentd) URL for the dev proxy. Under Aspire the AppHost injects the reference as an
// environment variable; standalone we fall back to the Host's default loopback address.
// (resource "agentd-host" → AGENTD_HOST_HTTP, or the service-discovery form services__agentd-host__http__0)
const hostUrl =
  process.env.AGENTD_HOST_HTTP ??
  process.env['services__agentd-host__http__0'] ??
  'http://127.0.0.1:7780'

const proxied = ['/api', '/bff', '/hubs', '/healthz', '/alive']

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  server: {
    proxy: Object.fromEntries(
      proxied.map((path) => [path, { target: hostUrl, changeOrigin: false, ws: path === '/hubs' }]),
    ),
  },
  build: {
    outDir: '../src/Agentd.Host/wwwroot',
    emptyOutDir: true,
  },
  test: {
    environment: 'jsdom',
    include: ['tests/**/*.spec.ts'],
  },
})
