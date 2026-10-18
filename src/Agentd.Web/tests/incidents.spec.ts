import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { setAntiforgeryUrl } from '../ClientApps/shared/api/http'
import IncidentsView from '../ClientApps/dashboard/views/IncidentsView.vue'
import IncidentView from '../ClientApps/dashboard/views/IncidentView.vue'
import { incidentRefreshDelayMs, useIncidentsStore } from '../ClientApps/dashboard/stores/incidents'
import { useSessionStore } from '../ClientApps/dashboard/stores/session'

// Incidents (incident-monitor.md §4): the list by status and app, and one incident with the 🚨 thread's actions.
const summary = (id: number, over: Record<string, unknown> = {}) => ({
  id, app: 'orders-api', kind: 'exception', title: 'NullReferenceException', message: 'Object reference not set', operation: 'GET /orders',
  status: 'Open', totalCount: 42, firstSeen: '2026-10-13T10:00:00Z', lastSeen: '2026-10-14T10:00:00Z', workItemId: null, analyzed: true, ...over,
})
const detail = (over: Record<string, unknown> = {}) => ({
  summary: summary(5, over), stack: 'at Orders.Get()', resultCode: null, operationId: 'op1', analysis: '**Likely cause** `src/Orders.cs:42`',
  analyzedAt: '2026-10-14T10:05:00Z', repository: 'sysmin', portalUrl: 'https://portal.azure.com/#resource/x/failures', hasThread: true,
})
const message = { id: 30, source: 'incident', sourceId: 5, title: '🚨 orders-api', recipient: null, kind: 'Question', markdown: '🔎 **Investigation of incident #5**',
  options: [], createdAt: '2026-10-14T10:05:00Z' }
let posts: { path: string; body: unknown }[]
let asked: string[]
let current: ReturnType<typeof detail>

const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/incidents', name: 'incidents', component: IncidentsView },
  { path: '/incidents/:id', name: 'incident', component: IncidentView, props: (r) => ({ id: Number(r.params.id) }) },
  { path: '/workitems/:id', name: 'work-item', component: { template: '<div />' } },
  { path: '/inbox/:source/:id', name: 'inbox-thread', component: { template: '<div />' } },
  { path: '/settings/prompts/:source/:sourceId', name: 'prompt-conversation', component: { template: '<div />' } },
] })

beforeEach(() => {
  setActivePinia(createPinia())
  setAntiforgeryUrl('/bff/antiforgery')
  useSessionStore().user = { name: 'local', roles: ['Admin'], provider: 'none' }
  posts = []
  asked = []
  current = detail()
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const url = new URL(req.url)
    asked.push(url.pathname)
    if (req.method === 'POST' && url.pathname !== '/bff/antiforgery') {
      posts.push({ path: url.pathname, body: JSON.parse(await req.text()) })
      return new Response(JSON.stringify({ outcome: 'filed' }), { status: 202 })
    }
    if (url.pathname === '/api/incidents') {
      return new Response(JSON.stringify([summary(5), summary(4, { status: 'Fixing', workItemId: 9001, app: 'billing' }), summary(3, { status: 'Ignored' })]))
    }
    if (url.pathname === '/api/incidents/5') return new Response(JSON.stringify(current))
    if (url.pathname === '/api/incidents/5/occurrences') {
      return new Response(JSON.stringify([{ at: '2026-10-14T09:59:00Z', message: 'boom', stack: 'at Orders.Get()', operation: 'GET /orders', operationId: 'op1',
        url: '/orders?id=1', resultCode: '500', properties: '{"tenant":"t1"}' }]))
    }
    if (url.pathname === '/api/inbox/incident/5') return new Response(JSON.stringify([message]))
    return new Response(JSON.stringify({ token: 't' }))
  }))
})
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

async function open(path: string) {
  const r = router()
  await r.push(path)
  const w = mount({ template: '<RouterView />' }, { global: { plugins: [r] } })
  await flushPromises()
  return w
}

describe('Incidents', () => {
  it('lists the active ones first, by status and app, with the Bug filed for each', async () => {
    const w = await open('/incidents')

    expect(w.findAll('[data-testid=incident-row]').map((r) => r.text())).toEqual([
      expect.stringContaining('#5 NullReferenceException in GET /orders'), expect.stringContaining('Bug #9001')])
    await w.findAll('[role=radio]').find((b) => b.text() === 'Ignored')!.trigger('click')
    expect(w.findAll('[data-testid=incident-row]').map((r) => r.text())).toEqual([expect.stringContaining('#3')])

    await w.findAll('[role=radio]').find((b) => b.text() === 'All')!.trigger('click')
    await w.get('select[aria-label=App]').setValue('billing')
    expect(w.findAll('[data-testid=incident-row]').map((r) => r.text())).toEqual([expect.stringContaining('#4')])
  })

  it('shows one incident: its analysis, stack and thread; Fix it is confirmed, then sent as the thread would', async () => {
    const confirm = vi.fn(() => true)
    vi.stubGlobal('confirm', confirm)
    const w = await open('/incidents/5')

    expect(w.get('h1').text()).toContain('NullReferenceException')
    expect(w.get('[aria-label=Investigation]').text()).toContain('Likely cause src/Orders.cs:42')
    await w.findAll('button').find((b) => b.text().includes('What the telemetry recorded'))!.trigger('click')
    expect(w.get('[data-testid=incident-stack]').text()).toBe('at Orders.Get()')
    expect(w.findAll('[data-testid=incident-message]').map((m) => m.text())).toEqual([expect.stringContaining('Investigation of incident #5')])
    expect(w.get('a[href="https://portal.azure.com/#resource/x/failures"]').attributes('rel')).toContain('noopener')
    const buttons = w.get('[aria-label=Actions]').findAll('button')
    expect(buttons.map((b) => b.text())).toEqual(['🛠 Fix it', '🙈 Ignore', '🔎 Investigate again'])

    await buttons[0]!.trigger('click')
    await flushPromises()
    expect(confirm).toHaveBeenCalledOnce()
    expect(posts.at(-1)).toEqual({ path: '/api/inbox/incident/5/reply', body: { text: '🛠 Fix it' } })

    await w.get('textarea[aria-label=Question]').setValue(' why only on Mondays? ')
    await w.get('form').trigger('submit')
    await flushPromises()
    expect(posts.at(-1)).toEqual({ path: '/api/inbox/incident/5/reply', body: { text: 'why only on Mondays?' } })
  })

  it('an ignored one offers reopen; a User sees no actions and no occurrences', async () => {
    current = detail({ status: 'Ignored' })
    let w = await open('/incidents/5')
    expect(w.get('[aria-label=Actions]').findAll('button').map((b) => b.text())).toEqual(['👀 Reopen'])
    await w.get('[aria-label=Actions] button').trigger('click')
    await flushPromises()
    expect(posts.at(-1)).toEqual({ path: '/api/inbox/incident/5/reply', body: { text: 'reopen' } })
    w.unmount()

    useSessionStore().user = { name: 'dev', roles: ['User'], provider: 'tailscale' }
    w = await open('/incidents/5')
    expect(w.find('[aria-label=Actions]').exists()).toBe(false)
    expect(w.find('[aria-label=Occurrences]').exists()).toBe(false)
  })

  it('reads the latest occurrences from Application Insights only when asked', async () => {
    const w = await open('/incidents/5')
    expect(asked).not.toContain('/api/incidents/5/occurrences')

    await w.get('[aria-label=Occurrences] button').trigger('click')
    await flushPromises()
    expect(w.findAll('[data-testid=incident-occurrence]').map((o) => o.text())).toEqual([expect.stringContaining('/orders?id=1 500')])
  })

  it("re-reads the list and the open incident once per burst of its Inbox messages", async () => {
    const incidents = useIncidentsStore()
    await incidents.load()
    await incidents.open(5)
    asked = []
    vi.useFakeTimers()
    for (const sourceId of [5, 9]) incidents.apply({ seq: 1, jobId: null, ts: '2026-10-14T10:00:00Z', type: 'inbox.added', payload: { source: 'incident', sourceId } })
    incidents.apply({ seq: 2, jobId: null, ts: '2026-10-14T10:00:00Z', type: 'inbox.added', payload: { source: 'idea', sourceId: 5 } })

    await vi.advanceTimersByTimeAsync(incidentRefreshDelayMs + 1)
    expect(asked.sort()).toEqual(['/api/inbox/incident/5', '/api/incidents', '/api/incidents/5'])
  })
})
