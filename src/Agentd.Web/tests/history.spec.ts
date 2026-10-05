import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { fromQuery, toQuery } from '../ClientApps/dashboard/stores/history'
import HistoryView from '../ClientApps/dashboard/views/HistoryView.vue'

const page = (total = 60) => ({ items: [], total, totalCapped: false, page: 1, pageSize: 25 })

beforeEach(() => setActivePinia(createPinia()))
afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('history filters', () => {
  it('round-trip through the query string', () => {
    const filters = { states: ['Failed'], repo: 'sysmin', q: 'WI-5613', from: '2026-10-01', to: '2026-10-03', page: 2 }
    expect(toQuery(filters)).toEqual({ state: 'Failed', repo: 'sysmin', q: 'WI-5613', from: '2026-10-01', to: '2026-10-03', page: '2' })
    expect(fromQuery(toQuery(filters))).toEqual(filters)
  })

  it('defaults to every final state and drops junk', () => {
    expect(fromQuery({ state: 'Sleeping', from: 'yesterday', page: '-3' })).toEqual({ states: ['Done', 'Failed', 'Cancelled'], repo: '', q: '', from: '', to: '', page: 1 })
    expect(toQuery(fromQuery({}))).toEqual({})
  })
})

describe('HistoryView', () => {
  it('loads from the URL and asks the server once per pause in typing', async () => {
    vi.useFakeTimers()
    const urls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
      urls.push(new URL(req.url).pathname + new URL(req.url).search)
      return new Response(JSON.stringify(req.url.includes('/api/config') ? { repositories: [] } : page()))
    }))
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/history', component: HistoryView }, { path: '/jobs/:id', name: 'job', component: { template: '<div />' } }, { path: '/workitems/:id', name: 'work-item', component: { template: '<div />' } }] })
    await router.push('/history?state=Failed&page=2')
    const w = mount(HistoryView, { global: { plugins: [router] } })
    await flushPromises()

    const history = () => urls.filter((u) => u.startsWith('/api/history'))
    expect(history()).toHaveLength(1)
    expect(history()[0]).toContain('state=Failed')
    expect(history()[0]).toContain('page=2')
    expect(w.text()).toContain('60 jobs')
    expect(w.text()).toContain('Page 2 of 3')

    const input = w.get('input[type="search"]')
    for (const text of ['W', 'WI', 'WI-56', 'WI-5613']) await input.setValue(text)
    await vi.advanceTimersByTimeAsync(299)
    expect(history()).toHaveLength(1)
    await vi.advanceTimersByTimeAsync(1)
    await flushPromises()

    expect(history()).toHaveLength(2)
    expect(history()[1]).toContain('q=WI-5613')
    expect(history()[1]).not.toContain('page=2')
    expect(router.currentRoute.value.query).toMatchObject({ state: 'Failed', q: 'WI-5613' })
  })
})
