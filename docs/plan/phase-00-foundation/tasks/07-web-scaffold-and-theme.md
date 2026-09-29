# T0.7 — Web scaffold, theme & app shell

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 0 | T0.1 | M | web/ |

## Goal
A Vue 3 + TypeScript app with the **agentd green theme** (light and dark), an empty shell, the
toolchain (lint, type-check, tests), and a build that outputs into the Host's `wwwroot`. It is
CSP-ready from the start: no inline scripts or styles.

## Files
- `web/package.json`: create. Scripts, `"engines": { "node": ">=24" }`, and `package-lock.json`.
- `web/vite.config.ts`: create. Vue plugin, Tailwind plugin, dev proxy, `build.outDir`.
- `web/tsconfig*.json`, `web/eslint.config.js`: create.
- `web/index.html`: create. **No inline script or style.**
- `web/public/theme-init.js`: create. Sets `data-theme` before first paint.
- `web/src/main.ts`, `App.vue`, `router.ts`: create.
- `web/src/stores/ui.ts`: create. Theme state.
- `web/src/styles/app.css`: create. Tailwind + daisyUI + the `agentd` / `agentd-dark` themes.
- `web/src/components/AppShell.vue`, `ThemeToggle.vue`: create.
- `web/src/views/HomeView.vue`: create. Placeholder: "No jobs yet".
- `web/tests/ui-store.spec.ts`: create.

## Implementation
1. `npm create vite@latest web -- --template vue-ts`, then add: `vue-router`, `pinia`,
   `tailwindcss` + `@tailwindcss/vite`, `daisyui`; dev dependencies `eslint` + `eslint-plugin-vue`
   + `typescript-eslint`, `vitest`, `@vue/test-utils`, `jsdom`, `vue-tsc`. Nothing else, per the
   [UI dependency policy](../../../ui/README.md#1-dependency-policy). `base-ui-vue` and
   `@microsoft/signalr` come in Phase 3.
2. `app.css`: copy the theme block from the [Design System §5](../../../design-system/README.md#5-theme-implementation)
   exactly (the two `@plugin "daisyui/theme"` blocks, `@theme` tokens, and the muted/brand-ink overrides).
3. `index.html`:
   ```html
   <!doctype html>
   <html lang="en">
     <head>
       <meta charset="UTF-8" />
       <meta name="viewport" content="width=device-width, initial-scale=1.0" />
       <title>agentd</title>
       <script src="/theme-init.js"></script>
     </head>
     <body class="bg-base-100 text-base-content">
       <div id="app"></div>
       <script type="module" src="/src/main.ts"></script>
     </body>
   </html>
   ```
4. `theme-init.js` (plain ES5, wrapped in try/catch): read `localStorage['agentd.theme']`
   (`agentd` | `agentd-dark` | `system`). Set or remove `document.documentElement.dataset.theme`.
5. The `ui` store (a setup store) holds `theme`, `setTheme()` (persists the choice and applies the
   attribute) and system-preference tracking. `ThemeToggle` uses a daisyUI `join` of 3 buttons.
6. `AppShell`: a daisyUI `navbar` with a green logo dot, nav links (Dashboard only for now), a
   connection-indicator placeholder, and the theme toggle; then `<router-view />`.
7. `vite.config.ts`:
   - `build.outDir = '../src/Agentd.Host/wwwroot'`, `emptyOutDir: true`;
   - the dev `server.proxy` sends `/api`, `/bff`, `/hubs` and `/healthz` to the Host URL, read from
     the environment variable the Aspire Vite integration provides (confirm the exact variable name
     against the pinned Aspire version), falling back to `http://127.0.0.1:7780`.
8. The runtime-only Vue build (the Vite default). **Never** alias `vue` to the full build.

## Tests
- vitest: the `ui` store persists the theme and applies `data-theme`, and `system` removes the attribute.
- `vue-tsc --noEmit` and `npm run lint` are clean.

## Done when
- [ ] `npm run dev` (standalone or through Aspire) shows the green shell, and the toggle switches
      light/dark with no flash on reload.
- [ ] `npm run build` writes to `src/Agentd.Host/wwwroot`, and the built `index.html` has no inline
      `<script>` or `<style>` (grep check).
- [ ] Lint, type-check and tests pass on Node 24.
