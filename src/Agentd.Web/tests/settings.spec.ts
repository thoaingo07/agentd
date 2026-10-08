import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { setAntiforgeryUrl } from '../ClientApps/shared/api/http'
import { useSessionStore } from '../ClientApps/dashboard/stores/session'
import SettingsAreaView from '../ClientApps/dashboard/views/SettingsAreaView.vue'
import SettingsView from '../ClientApps/dashboard/views/SettingsView.vue'

let paths: string[]
const json = (body: unknown) => new Response(JSON.stringify(body))
const router = () => createRouter({
  history: createMemoryHistory(),
  routes: [
    { path: '/settings', name: 'settings', component: { template: '<div />' } },
    { path: '/settings/:area', name: 'settings-area', component: { template: '<div />' } },
  ],
})

beforeEach(() => {
  setActivePinia(createPinia())
  setAntiforgeryUrl('/bff/antiforgery')
  paths = []
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const path = new URL(req.url).pathname
    paths.push(`${req.method} ${path}`)
    if (path === '/bff/antiforgery') return json({ token: 'admin-xsrf' })
    if (path === '/api/settings/review') return json([{ step: 'database', title: 'Database', required: true, check: { ok: true, message: 'Connected.', fix: null } }])
    if (path === '/api/settings/database') return json({ connectionString: { set: true, updatedAt: '2026-10-07T10:00:00Z', updatedBy: 'local' } })
    return new Response(JSON.stringify({ title: 'Not found' }), { status: 404 })
  }))
})
afterEach(() => vi.unstubAllGlobals())

describe('Settings', () => {
  it('area pages use /api/settings, not the setup session', async () => {
    const view = mount(SettingsAreaView, { props: { area: 'database' }, global: { plugins: [router()] } })
    await flushPromises()

    expect(paths).toContain('GET /api/settings/database')
    expect(paths.some((p) => p.includes('/api/setup/'))).toBe(false)
    expect(view.text()).toContain('updated')
  })

  it('Health is the live review without Finish', async () => {
    const view = mount(SettingsAreaView, { props: { area: 'health' }, global: { plugins: [router()] } })
    await flushPromises()

    expect(view.find('h2').text()).toBe('Health')
    expect(view.text()).toContain('Connected.')
    expect(view.findAll('button').some((b) => b.text() === 'Finish setup')).toBe(false)
  })

  it('only Admins see the configuration links', async () => {
    const session = useSessionStore()
    session.user = { name: 'dev@example.com', roles: ['User'], provider: 'cloudflare' }
    const view = mount(SettingsView, { global: { plugins: [router()] } })
    await flushPromises()
    expect(view.text()).not.toContain('Configuration')

    session.user = { name: 'local', roles: ['Admin'], provider: 'local' }
    await flushPromises()
    expect(view.findAll('a').map((a) => a.text())).toEqual(expect.arrayContaining(['Health', 'Database', 'Azure DevOps', 'Models', 'Repositories']))
  })
})
