import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import SystemLogView from '../ClientApps/dashboard/views/SystemLogView.vue'

const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/', component: { template: '<div />' } },
  { path: '/settings', name: 'settings', component: { template: '<div />' } },
  { path: '/jobs/:id', name: 'job', component: { template: '<div />' } },
] })
const entry = (id: number, level = 'Information', jobId: number | null = null, exception: string | null = null) =>
  ({ id, at: '2026-10-26T12:00:00Z', level, category: 'Agentd.Application.Monitor.PrMonitor', message: `entry ${id}`, exception, jobId })
let asked: URLSearchParams[]

beforeEach(() => {
  setActivePinia(createPinia())
  asked = []
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const params = new URL(req.url).searchParams
    asked.push(params)
    return new Response(JSON.stringify(params.has('before')
      ? { entries: [entry(1)], hasMore: false }
      : { entries: [entry(3, 'Error', 42, 'System.TimeoutException: slow'), entry(2, 'Warning')], hasMore: true }))
  }))
})
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('System log', () => {
  it('shows entries newest first with their level, source and job, and loads older ones', async () => {
    const w = mount(SystemLogView, { global: { plugins: [router()] } })
    await flushPromises()

    const rows = w.findAll('[data-testid=log-entry]')
    expect(rows.map((r) => r.find('.whitespace-pre-wrap').text())).toEqual(['entry 3', 'entry 2'])
    expect(rows[0]!.text()).toContain('PrMonitor')
    expect(rows[0]!.get('a').attributes('href')).toBe('/jobs/42')
    expect(rows[0]!.get('details pre').text()).toBe('System.TimeoutException: slow')

    await w.findAll('button').find((b) => b.text() === 'Older entries')!.trigger('click')
    await flushPromises()
    expect(asked.at(-1)?.get('before')).toBe('2')
    expect(w.findAll('[data-testid=log-entry]')).toHaveLength(3)
    expect(w.find('button').exists()).toBe(false)
  })

  it('filters by level, source and text, and refreshes only while at the top', async () => {
    vi.useFakeTimers()
    const w = mount(SystemLogView, { global: { plugins: [router()] } })
    await flushPromises()

    await w.get('select[aria-label="Level"]').setValue('Warning')
    await w.get('input[aria-label="Source"]').setValue('monitor')
    await w.get('input[aria-label="Search messages"]').setValue('timeout')
    await vi.advanceTimersByTimeAsync(301)
    expect(Object.fromEntries(asked.at(-1)!)).toEqual({ level: 'Warning', limit: '100', category: 'monitor', q: 'timeout' })

    const before = asked.length
    await vi.advanceTimersByTimeAsync(5001)
    expect(asked.length).toBe(before + 1)

    await w.findAll('button').find((b) => b.text() === 'Older entries')!.trigger('click')
    await flushPromises()
    const reading = asked.length
    await vi.advanceTimersByTimeAsync(15_000)
    expect(asked.length).toBe(reading)   // reading older entries: no refresh pulls the page away
  })
})
