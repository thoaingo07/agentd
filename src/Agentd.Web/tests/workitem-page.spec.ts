import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { setEventConnectionFactory, type EventConnection } from '../ClientApps/shared/api/hub'
import type { AgentEvent, JobSummary } from '../ClientApps/shared/api/types'
import JobView from '../ClientApps/dashboard/views/JobView.vue'
import WorkItemView from '../ClientApps/dashboard/views/WorkItemView.vue'
import { useConnectionStore } from '../ClientApps/dashboard/stores/connection'

const job = (id: number, state: string): JobSummary => ({
  id, workItemId: 5613, title: 'Refine', repo: 'sysmin', branch: `ai/5613-r${id}`, state, phase: null, startedAt: '2026-10-03T16:00:00Z',
  elapsedSeconds: 600, prUrl: null, waitingSince: null, planStatus: 'Approved', handoff: 'None', fixRounds: 0, lastError: null, completedAt: null, pendingPermissions: 0,
})
const ev = (seq: number, jobId: number, type: string, payload: object = {}): AgentEvent => ({ seq, jobId, ts: '2026-10-03T16:00:00Z', type, payload })
const jobs = [job(1, 'Done'), job(2, 'Running')]
let paths: string[]
let unsubscribed: string[]

const router = () => createRouter({
  history: createMemoryHistory(),
  routes: [
    { path: '/', component: { template: '<div />' } },
    { path: '/jobs/:id', name: 'job', component: JobView, props: (r) => ({ id: Number(r.params.id) }) },
    { path: '/workitems/:id', name: 'work-item', component: { template: '<div />' } },
  ],
})

beforeEach(async () => {
  setActivePinia(createPinia())
  paths = []
  unsubscribed = []
  const fake: EventConnection = {
    start: async () => {}, stop: async () => {}, subscribe: async () => {}, unsubscribe: async (s) => void unsubscribed.push(s),
    onEvent: () => {}, onReconnecting: () => {}, onReconnected: () => {}, onClose: () => {},
  }
  setEventConnectionFactory(() => fake)
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const path = new URL(req.url).pathname
    paths.push(path)
    const run = Number(path.split('/')[3])
    const body = path === '/api/workitems/5613' ? { workItemId: 5613, title: 'Refine', repo: 'sysmin', jobs, pullRequests: [], conversations: [], firstSeenAt: '2026-10-03T16:00:00Z', lastActivityAt: '2026-10-03T18:00:00Z' }
      : path.endsWith('/timeline') ? { events: [ev(5, 1, 'JobQueued'), ev(9, 2, 'JobStarted')], oldestSeq: 5, newestSeq: 9, hasMore: false }
        : path.endsWith('/conversation') || path === '/api/ideas' ? []
          : /^\/api\/jobs\/\d+\/events$/.test(path) ? { events: [ev(run * 100, run, 'agent.text', { text: `run ${run} working` }), ev(run * 100 + 1, run, 'step.started', { line: `🔨 Implement · run ${run}` })], oldestSeq: run * 100, newestSeq: run * 100 + 1, hasMore: false }
            : /^\/api\/jobs\/\d+\/diff$/.test(path) ? { baseRef: 'develop', headRef: `ai/5613-r${run}`, files: ['a.cs'], unifiedDiff: null, truncated: true }
              : /^\/api\/jobs\/\d+$/.test(path) ? { job: jobs.find((j) => j.id === run), permissions: [], conversations: [], usage: null, estimate: null, attempt: 1, resumeCount: 0, pendingMessages: 0, lastActivity: null }
                : null
    return new Response(JSON.stringify(body ?? { title: 'Not found' }), { status: body ? 200 : 404 })
  }))
  await useConnectionStore().start()
})
afterEach(() => vi.unstubAllGlobals())

describe('work item page', () => {
  it('shows the active run\'s transcript with its tool calls, actions and step, and switches runs in place', async () => {
    const r = router()
    await r.push('/workitems/5613')
    const view = mount(WorkItemView, { props: { id: 5613 }, global: { plugins: [r] } })
    await flushPromises()

    expect(view.text()).toContain('run 2 working')
    expect(view.get('[data-testid=step]').text()).toBe('🔨 Implement · run 2')
    expect(view.get('[role=radio][aria-checked=true]').text()).toBe('Run 2 (rework)')
    expect(view.findAll('button').map((b) => b.text())).toEqual(expect.arrayContaining(['Pause', 'Cancel']))
    expect(view.find('textarea').exists()).toBe(true)

    await view.findAll('[role=radio]').find((b) => b.text() === 'Run 1')!.trigger('click')
    await flushPromises()

    expect(r.currentRoute.value.query.run).toBe('1')
    expect(view.text()).toContain('run 1 working')
    expect(view.text()).not.toContain('run 2 working')
    expect(view.findAll('button').some((b) => b.text() === 'Cancel')).toBe(false)
    expect(unsubscribed).not.toContain('2')   // the active run keeps streaming for the timeline

    await view.findAll('[role=tab]').find((t) => t.text() === 'Diff')!.trigger('click')
    await flushPromises()
    expect(paths).toContain('/api/jobs/1/diff')
  })

  it('an old job link opens its work item with that run picked', async () => {
    const r = router()
    await r.push('/jobs/2')
    mount({ template: '<RouterView />' }, { global: { plugins: [r] } })
    await flushPromises()

    expect(r.currentRoute.value.fullPath).toBe('/workitems/5613?run=2')
  })
})
