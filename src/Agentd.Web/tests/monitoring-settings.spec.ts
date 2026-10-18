import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { setAntiforgeryUrl } from '../ClientApps/shared/api/http'
import MonitoringSettings from '../ClientApps/dashboard/components/MonitoringSettings.vue'
import { useSetupStore } from '../ClientApps/shared/setup/stores/setup'

// Settings → Monitoring (incident-monitor.md §4.2): the apps in agentd.json, Test connection, and Save (used after a restart).
const ordersId = '/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Insights/components/orders-ai'
let sent: { method: string; path: string; body: unknown }[]

beforeEach(() => {
  setActivePinia(createPinia())
  setAntiforgeryUrl('/bff/antiforgery')
  sent = []
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const url = new URL(req.url)
    if (url.pathname === '/api/config') return new Response(JSON.stringify({ repositories: [{ name: 'sysmin', organization: 'o', project: 'p' }, { name: 'billing', organization: 'o', project: 'p' }] }))
    if (url.pathname === '/api/settings/monitor' && req.method === 'GET') {
      return new Response(JSON.stringify({ enabled: false, tenantId: null, running: false, enabledOverriddenBy: null,
        apps: [{ name: 'orders-api', resourceId: ordersId, repository: 'sysmin', roleName: null }] }))
    }
    if (req.method !== 'GET' && url.pathname !== '/bff/antiforgery') {
      sent.push({ method: req.method, path: url.pathname, body: JSON.parse(await req.text()) })
      return new Response(JSON.stringify(url.pathname.endsWith('/test')
        ? { ok: false, message: 'Application Insights refused the query: AuthorizationFailed', fix: 'Give agentd’s identity Reader.' }
        : { restartRequired: true }))
    }
    return new Response(JSON.stringify({ token: 't' }))
  }))
})
afterEach(() => vi.unstubAllGlobals())

describe('Settings → Monitoring', () => {
  it('tests an app and shows the fix when Azure refuses', async () => {
    const w = mount(MonitoringSettings)
    await flushPromises()

    expect(w.get('[data-testid=monitor-running]').text()).toBe('Running now: off')
    await w.get('[data-testid=monitor-app]').findAll('button').find((b) => b.text() === 'Test connection')!.trigger('click')
    await flushPromises()

    expect(sent).toEqual([{ method: 'POST', path: '/api/settings/monitor/test', body: { name: 'orders-api', resourceId: ordersId, repository: 'sysmin', roleName: null } }])
    expect(w.get('[role=status]').text()).toContain('AuthorizationFailed')
    expect(w.get('[role=status]').text()).toContain('Reader')
  })

  it('adds an app, turns the monitor on and saves the whole list; a restart is then asked for', async () => {
    const w = mount(MonitoringSettings)
    await flushPromises()

    await w.findAll('button').find((b) => b.text() === 'Add app')!.trigger('click')
    const added = w.findAll('[data-testid=monitor-app]')[1]!
    await added.get('input[aria-label=Name]').setValue(' billing ')
    await added.get('select[aria-label=Repository]').setValue('billing')
    await added.get('input[aria-label="Resource id"]').setValue(ordersId.replace('orders', 'billing'))
    await w.get('input[aria-label="Monitor on"]').setValue(true)
    await w.get('input[aria-label="Tenant id"]').setValue('t1')
    await w.findAll('button').find((b) => b.text() === 'Save')!.trigger('click')
    await flushPromises()

    expect(sent.at(-1)).toEqual({ method: 'PUT', path: '/api/settings/monitor', body: { enabled: true, tenantId: 't1', apps: [
      { name: 'orders-api', resourceId: ordersId, repository: 'sysmin', roleName: null },
      { name: 'billing', resourceId: ordersId.replace('orders', 'billing'), repository: 'billing', roleName: null },
    ] } })
    expect(w.get('[role=status]').text()).toBe('Saved: restart agentd to use it.')
    expect(useSetupStore().restartRequired).toBe(true)
  })

  it('removing an app leaves it out of the saved list', async () => {
    const w = mount(MonitoringSettings)
    await flushPromises()

    await w.get('[data-testid=monitor-app]').findAll('button').find((b) => b.text() === 'Remove')!.trigger('click')
    await w.findAll('button').find((b) => b.text() === 'Save')!.trigger('click')
    await flushPromises()

    expect(sent.at(-1)).toEqual({ method: 'PUT', path: '/api/settings/monitor', body: { enabled: false, tenantId: null, apps: [] } })
  })
})
