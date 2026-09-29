import { existsSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'

// Backend integration (https://vite.dev/guide/backend-integration). The HTML shell is a Razor view in
// this project; Agentd.Web.Vite.ViteHelper renders the tags for an app from the manifest (production)
// or from this dev server through the Host proxy (development).
//
// Every ClientApps/<app>/main.ts is its own SPA entry; ClientApps/shared holds code/styles used by several.
const clientApps = readdirSync(resolve(__dirname, 'ClientApps'), { withFileTypes: true })
  .filter((d) => d.isDirectory() && existsSync(resolve(__dirname, 'ClientApps', d.name, 'main.ts')))
  .map((d) => d.name)

// Razor class library static assets are served under /_content/<assembly>/.
const base = '/_content/Agentd.Web/'

export default defineConfig({
  base,
  plugins: [vue(), tailwindcss()],
  server: {
    // HMR goes through the Host proxy under the same prefix: /_content/Agentd.Web/__vite_hmr
    hmr: { path: '__vite_hmr' },
  },
  build: {
    manifest: 'manifest.json', // → wwwroot/manifest.json (keys: ClientApps/<app>/main.ts)
    outDir: 'wwwroot',
    emptyOutDir: true,
    rollupOptions: {
      input: Object.fromEntries(clientApps.map((app) => [app, `ClientApps/${app}/main.ts`])),
    },
  },
  test: {
    environment: 'jsdom',
    include: ['tests/**/*.spec.ts', 'ClientApps/**/*.spec.ts'],
  },
})
