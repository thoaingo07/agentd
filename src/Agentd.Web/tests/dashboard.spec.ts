import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { JobSummary } from '../ClientApps/shared/api/types'
import JobTable from '../ClientApps/dashboard/components/JobTable.vue'
import RunWorkItemModal from '../ClientApps/dashboard/components/RunWorkItemModal.vue'
import DashboardView from '../ClientApps/dashboard/views/DashboardView.vue'
import { useJobsStore } from '../ClientApps/dashboard/stores/jobs'

const job = (id: number, state: string, startedAt = '2026-10-04T10:00:00Z'): JobSummary => ({
  id, workItemId: 5600 + id, title: `job ${id}`, repo: 'sysmin', branch: null, state, phase: null, startedAt,
  elapsedSeconds: 60, prUrl: null, waitingSince: null, planStatus: 'NotRequired', handoff: 'None', fixRounds: 0, lastError: null, completedAt: null,
})

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: DashboardView },
      { path: '/jobs/:id', name: 'job', component: { template: '<div />' } },
    ],
  })
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ tag: 'ai-workflow', repositories: [], messagingProviders: [], maxConcurrent: 2 }))))
})
afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('JobTable', () => {
  it('flashes a row when its state changes', async () => {
    vi.useFakeTimers()
    const router = makeRouter()
    const w = mount(JobTable, { props: { jobs: [job(1, 'Running')] }, global: { plugins: [router] } })
    expect(w.find('[data-flash]').exists()).toBe(false)

    await w.setProps({ jobs: [job(1, 'WaitingForHuman')] })
    expect(w.find('tbody tr').attributes('data-flash')).toBe('true')
    expect(w.find('tbody tr').classes()).toContain('bg-warning/10')

    vi.advanceTimersByTime(700)
    await flushPromises()
    expect(w.find('[data-flash]').exists()).toBe(false)
  })
})

describe('DashboardView', () => {
  it('shows waiting jobs first and keeps the filter in the query string', async () => {
    const router = makeRouter()
    await router.push('/')
    const jobs = useJobsStore()
    for (const j of [job(1, 'Running', '2026-10-04T09:00:00Z'), job(2, 'WaitingForHuman'), job(3, 'Queued')]) jobs.byId.set(j.id, j)
    const w = mount(DashboardView, { global: { plugins: [router] } })
    await flushPromises()

    expect(w.findAll('tbody tr').map((r) => r.text())).toEqual([expect.stringContaining('job 2'), expect.stringContaining('job 1'), expect.stringContaining('job 3')])

    await w.findAll('[role="group"] button, [data-pressed], button').find((b) => b.text() === 'Waiting')!.trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query.filter).toBe('waiting')
    expect(w.findAll('tbody tr')).toHaveLength(1)

    await router.replace({ query: { filter: 'queued' } })
    await flushPromises()
    expect(w.findAll('tbody tr').map((r) => r.text())).toEqual([expect.stringContaining('job 3')])
  })
})

describe('RunWorkItemModal', () => {
  it.each(['abc', '-1', '0', '1.5', ''])('rejects %j without calling the server', async (input) => {
    const router = makeRouter()
    const w = mount(RunWorkItemModal, { props: { open: true }, global: { plugins: [router] }, attachTo: document.body })
    await w.get('input').setValue(input)
    await w.get('form').trigger('submit')
    await flushPromises()

    expect(w.get('#wi-error').text()).toContain('work item number')
    expect(vi.mocked(fetch)).not.toHaveBeenCalled()
    w.unmount()
  })
})
