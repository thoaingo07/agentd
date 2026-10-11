import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { setAntiforgeryUrl } from '../ClientApps/shared/api/http'
import { useSessionStore } from '../ClientApps/dashboard/stores/session'
import SettingsAreaView from '../ClientApps/dashboard/views/SettingsAreaView.vue'
import SettingsLayout from '../ClientApps/dashboard/components/SettingsLayout.vue'
import AdoConnectionPanel from '../ClientApps/dashboard/components/AdoConnectionPanel.vue'

let paths: string[]
let adoConnections: object[]
let bodies: Record<string, unknown>
let storage: object
const json = (body: unknown) => new Response(JSON.stringify(body))
const router = () => createRouter({
  history: createMemoryHistory(),
  routes: [
    { path: '/settings', name: 'settings', component: { template: '<div />' } },
    { path: '/settings/log', name: 'system-log', component: { template: '<div />' } },
    { path: '/settings/:area', name: 'settings-area', component: { template: '<div />' } },
  ],
})

beforeEach(() => {
  setActivePinia(createPinia())
  setAntiforgeryUrl('/bff/antiforgery')
  paths = []
  bodies = {}
  storage = {
    url: 'https://acct.r2.cloudflarestorage.com/agentd', publicUrl: null, urlOverriddenBy: null, publicUrlOverriddenBy: 'an environment variable or the command line',
    accessKeyId: { set: true, updatedAt: null, updatedBy: 'environment', overriddenBy: 'Key Vault (agentd-prd.vault.azure.net)' },
    secretAccessKey: { set: false, updatedAt: null, updatedBy: null, overriddenBy: null },
  }
  adoConnections = [{ identityId: 'aaaa', uniqueName: 'dev@example.com', displayName: 'Dev One', failed: false, lastError: null, connectedAt: '2026-10-09T10:00:00Z', kind: 'OAuth', commitName: null, commitEmail: null, authorName: 'Dev One', authorEmail: 'dev@example.com' }]
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const path = new URL(req.url).pathname
    paths.push(`${req.method} ${path}`)
    if (path === '/bff/antiforgery') return json({ token: 'admin-xsrf' })
    if (path === '/api/settings/review') return json([{ step: 'database', title: 'Database', required: true, check: { ok: true, message: 'Connected.', fix: null } }])
    if (path === '/api/me/ado-connections' && req.method === 'GET') return json({ available: true, connections: adoConnections })
    if (req.method === 'POST' || req.method === 'PUT') bodies[`${req.method} ${path}`] = await req.json()
    if (path === '/api/me/ado-connections/pat') {
      adoConnections = [{ identityId: 'bbbb', uniqueName: 'pat@example.com', displayName: 'Pat User', failed: false, lastError: null, connectedAt: '2026-10-10T10:00:00Z', kind: 'Pat', commitName: 'Pat', commitEmail: null, authorName: 'Pat', authorEmail: 'pat@example.com' }]
      return json(adoConnections[0])
    }
    if (path.endsWith('/commit-author')) return json(adoConnections[0])
    if (path.startsWith('/api/me/ado-connections/') && req.method === 'DELETE') {
      adoConnections = []
      return new Response(null, { status: 204 })
    }
    if (path === '/api/settings/artifact-storage' && req.method === 'GET') return json(storage)
    if (path === '/api/settings/artifact-storage') return json({ restartRequired: true })
    if (path === '/api/settings/artifact-storage/test') return json({ ok: true, message: 'Uploaded, read back and deleted a test page.', fix: null })
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

  it('only Admins see the vertical menu of configuration pages', async () => {
    const session = useSessionStore()
    session.user = { name: 'dev@example.com', roles: ['User'], provider: 'cloudflare' }
    const view = mount(SettingsLayout, { global: { plugins: [router()] } })
    await flushPromises()
    expect(view.text()).not.toContain('Configuration')

    session.user = { name: 'local', roles: ['Admin'], provider: 'local' }
    await flushPromises()
    const menu = view.get('nav[aria-label=Settings] ul.menu')
    expect(menu.findAll('a').map((a) => a.text())).toEqual(['General', 'Health', 'Database', 'Azure DevOps', 'Git access', 'Claude', 'Models', 'Chat', 'Repositories', 'Artifacts', 'System log'])
  })

  it('an area page has the vertical menu with its own entry current', async () => {
    useSessionStore().user = { name: 'local', roles: ['Admin'], provider: 'local' }
    const r = router()
    await r.push('/settings/database')
    const view = mount(SettingsLayout, { global: { plugins: [r] } })
    await flushPromises()

    const current = view.get('nav[aria-label=Settings] a[aria-current=page]')
    expect([current.text(), current.classes()]).toEqual(['Database', expect.arrayContaining(['menu-active'])])
  })

  it('Artifacts: shows the storage, what overrides it, and saves and tests without echoing keys', async () => {
    const view = mount(SettingsAreaView, { props: { area: 'artifacts' }, global: { plugins: [router()] } })
    await flushPromises()

    expect((view.get('input[type=url]').element as HTMLInputElement).value).toBe('https://acct.r2.cloudflarestorage.com/agentd')
    expect(view.text()).toContain("From an environment variable or the command line: saving here doesn't change it.")
    expect(view.get('[data-testid=overridden]').text()).toBe('from Key Vault (agentd-prd.vault.azure.net): change it there')
    expect(view.findAll('button').filter((b) => b.text() === 'Replace')).toHaveLength(0)

    await view.findAll('input[type=url]')[1]!.setValue(' https://agentd.thoaingo.com ')
    await view.get('input[type=password]').setValue('the-secret')
    await view.findAll('button').find((b) => b.text() === 'Test connection')!.trigger('click')
    await flushPromises()
    expect(view.text()).toContain('Uploaded, read back and deleted a test page.')
    await view.findAll('button').find((b) => b.text() === 'Save')!.trigger('click')
    await flushPromises()

    const body = { url: 'https://acct.r2.cloudflarestorage.com/agentd', publicUrl: 'https://agentd.thoaingo.com', accessKeyId: null, secretAccessKey: 'the-secret' }
    expect([bodies['POST /api/settings/artifact-storage/test'], bodies['PUT /api/settings/artifact-storage']]).toEqual([body, body])
    expect((view.get('input[type=password]').element as HTMLInputElement).value).toBe('')
    expect(view.text()).toContain('agentd daemon restart')
  })

  it('your Azure DevOps: shows the result of the sign-in, who you are connected as, and disconnects', async () => {
    const r = router()
    await r.push('/settings?ado=connected')
    const view = mount(AdoConnectionPanel, { global: { plugins: [r] } })
    await flushPromises()

    expect(view.get('[role=status]').text()).toContain('Connected. agentd will act as you')
    expect(view.get('[data-testid=ado-connection]').text()).toContain('Connected as Dev One (dev@example.com)')
    expect(view.get('a[href="/bff/ado/connect"]').text()).toBe('Reconnect with Microsoft')

    await view.findAll('button').find((b) => b.text() === 'Disconnect')!.trigger('click')
    await flushPromises()
    expect(paths).toContain('DELETE /api/me/ado-connections/aaaa')
    expect(view.find('[data-testid=ado-connection]').exists()).toBe(false)
    expect(view.get('a[href="/bff/ado/connect"]').text()).toBe('Connect with Microsoft')
  })

  it('your Azure DevOps: saves a personal access token, never shows it again, and changes the commit author', async () => {
    const view = mount(AdoConnectionPanel, { global: { plugins: [router()] } })
    await flushPromises()
    expect(view.get('[data-testid=commit-author]').text()).toContain('Commits as Dev One <dev@example.com>')

    const form = view.get('[data-testid=ado-pat]')
    await form.get('input[type=password]').setValue('  secret-pat  ')
    await form.findAll('input')[1]!.setValue('Pat')
    await form.trigger('submit')
    await flushPromises()

    expect(bodies['POST /api/me/ado-connections/pat']).toEqual({ token: '  secret-pat  ', commitName: 'Pat', commitEmail: '' })
    expect((view.get('[data-testid=ado-pat] input[type=password]').element as HTMLInputElement).value).toBe('')
    expect(view.text()).not.toContain('secret-pat')
    expect(view.get('[data-testid=ado-connection]').text()).toContain('access token')

    await view.findAll('button').find((b) => b.text() === 'Change')!.trigger('click')
    await view.get('[data-testid=ado-connection] input[type=email]').setValue('pat@work.example')
    await view.get('[data-testid=ado-connection] form').trigger('submit')
    await flushPromises()
    expect(bodies['PUT /api/me/ado-connections/bbbb/commit-author']).toEqual({ name: 'Pat', email: 'pat@work.example' })
  })
})
