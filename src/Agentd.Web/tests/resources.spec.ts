import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { JobSummary, Resources } from '../ClientApps/shared/api/types'
import { bytes } from '../ClientApps/shared/utils/format'
import { resourcesPollMs, useResourcesStore } from '../ClientApps/dashboard/stores/resources'
import AppShell from '../ClientApps/dashboard/components/AppShell.vue'
import HostPanel from '../ClientApps/dashboard/components/HostPanel.vue'
import JobTable from '../ClientApps/dashboard/components/JobTable.vue'

const sample: Resources = {
  machine: { cpuPercent: 35, memoryTotal: 16 * 2 ** 30, memoryAvailable: 6 * 2 ** 30, diskTotal: 500 * 2 ** 30, diskFree: 3 * 2 ** 30, lowMemory: false, lowDisk: true, at: '2026-10-06T10:00:00Z',
    details: { cores: 16, load1: 0.77, load5: 1.43, load15: 0.9, swapTotal: 8 * 2 ** 30, swapFree: 8 * 2 ** 30, uptimeSeconds: 130_812,
      disks: [{ mount: '/', total: 913 * 2 ** 30, free: 423 * 2 ** 30, home: true }, { mount: '/data', total: 916 * 2 ** 30, free: 867 * 2 ** 30, home: false }] } },
  jobs: [{ jobId: 7, cpuPercent: 180, memoryBytes: 2.1 * 2 ** 30, worktreeBytes: 450 * 2 ** 20, at: '2026-10-06T10:00:00Z' }],
}
const job = (id: number): JobSummary => ({
  id, workItemId: 5617, title: 'Deploy', repo: 'sysmin', branch: null, state: 'Running', phase: 'implement', startedAt: '2026-10-06T09:00:00Z',
  elapsedSeconds: 60, prUrl: null, waitingSince: null, planStatus: 'NotRequired', handoff: 'None', fixRounds: 0, lastError: null, completedAt: null, pendingPermissions: 0,
})
const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/', component: { template: '<div />' } },
  { path: '/settings', name: 'settings', component: { template: '<div />' } },
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

    const machine = w.get('[data-testid=machine]')
    const [short, full] = machine.findAll('span').map((s) => s.text())
    expect(machine.text().startsWith('⚠️')).toBe(true)
    expect(short).toBe('CPU 35% · 6 GB')   // phones
    expect(full).toBe('CPU 35% · RAM 6 GB free · disk 3 GB free')
    expect(machine.classes()).toContain('font-semibold')
    expect(machine.attributes('href')).toBe('/settings')   // the Host panel
  })

  it('the Host panel shows CPU and load, RAM, swap and every disk', async () => {
    const w = mount(HostPanel)
    await flushPromises()

    expect(w.text()).toContain('16 cores · load 0.77 / 1.43 / 0.90')
    expect(w.text()).toContain('6 GB free of 16 GB')
    expect(w.text()).toContain('0 KB used of 8 GB')
    expect(w.findAll('[data-testid=disk]').map((d) => d.text())).toEqual([
      expect.stringContaining("423 GB free of 913 GB · agentd's home"),
      expect.stringContaining('867 GB free of 916 GB'),
    ])
    expect(w.text()).toContain('Up 1 d 12 h')
    expect(w.text()).toContain('CPU35%')
    expect(w.text()).toContain('RAM63%')   // used, of 16 GB
    expect(w.get('[role=status]').text()).toContain('Disk space is running low')
  })

  it('the dashboard table shows each running job\'s CPU and RAM', async () => {
    await useResourcesStore().load()
    const w = mount(JobTable, { props: { jobs: [job(7), job(8)] }, global: { plugins: [router()] } })

    const cells = w.findAll('tbody tr').map((r) => r.text())
    expect(cells[0]).toContain('180% · 2.1 GB')
    expect(cells[1]).toContain('—')
  })
})
