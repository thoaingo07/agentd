import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { JobSummary, Resources } from '../ClientApps/shared/api/types'
import { bytes } from '../ClientApps/shared/utils/format'
import { resourcesPollMs, useResourcesStore } from '../ClientApps/dashboard/stores/resources'
import AppShell from '../ClientApps/dashboard/components/AppShell.vue'
import JobTable from '../ClientApps/dashboard/components/JobTable.vue'

const sample: Resources = {
  machine: { cpuPercent: 35, memoryTotal: 16 * 2 ** 30, memoryAvailable: 6 * 2 ** 30, diskTotal: 500 * 2 ** 30, diskFree: 3 * 2 ** 30, lowMemory: false, lowDisk: true, at: '2026-10-06T10:00:00Z' },
  jobs: [{ jobId: 7, cpuPercent: 180, memoryBytes: 2.1 * 2 ** 30, worktreeBytes: 450 * 2 ** 20, at: '2026-10-06T10:00:00Z' }],
}
const job = (id: number): JobSummary => ({
  id, workItemId: 5617, title: 'Deploy', repo: 'sysmin', branch: null, state: 'Running', phase: 'implement', startedAt: '2026-10-06T09:00:00Z',
  elapsedSeconds: 60, prUrl: null, waitingSince: null, planStatus: 'NotRequired', handoff: 'None', fixRounds: 0, lastError: null, completedAt: null, pendingPermissions: 0,
})
const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/', component: { template: '<div />' } },
  { path: '/jobs/:id', name: 'job', component: { template: '<div />' } },
  { path: '/workitems/:id', name: 'work-item', component: { template: '<div />' } },
  { path: '/:p(.*)*', name: 'any', component: { template: '<div />' } },
] })

beforeEach(() => {
  setActivePinia(createPinia())
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(sample))))
})
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('resources', () => {
  it('formats bytes like the chat does', () => {
    expect([bytes(1536), bytes(450 * 2 ** 20), bytes(2 * 2 ** 30), bytes(2.1 * 2 ** 30), bytes(null)]).toEqual(['1 KB', '450 MB', '2 GB', '2.1 GB', '—'])
  })

  it('polls only while something watches, once however many watch', async () => {
    vi.useFakeTimers()
    const store = useResourcesStore()
    const stopA = store.watch()
    const stopB = store.watch()
    await vi.advanceTimersByTimeAsync(resourcesPollMs * 2 + 1)
    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(3)   // at once, then every 10 s: one poller for both
    expect(store.byJob.get(7)?.cpuPercent).toBe(180)

    stopA()
    stopA()   // stopping twice doesn't steal B's watch
    await vi.advanceTimersByTimeAsync(resourcesPollMs + 1)
    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(4)
    stopB()
    await vi.advanceTimersByTimeAsync(resourcesPollMs * 3)
    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(4)
  })

  it('the header shows the machine, flagged (not only by colour) when disk or memory is low', async () => {
    const w = mount(AppShell, { global: { plugins: [router()] } })
    await flushPromises()

    const machine = w.find('[data-testid=machine]')
    expect(machine.text()).toBe('⚠️ CPU 35% · RAM 6 GB free · disk 3 GB free')
    expect(machine.classes()).toContain('font-semibold')
  })

  it('the dashboard table shows each running job\'s CPU and RAM', async () => {
    await useResourcesStore().load()
    const w = mount(JobTable, { props: { jobs: [job(7), job(8)] }, global: { plugins: [router()] } })

    const cells = w.findAll('tbody tr').map((r) => r.text())
    expect(cells[0]).toContain('180% · 2.1 GB')
    expect(cells[1]).toContain('—')
  })
})
