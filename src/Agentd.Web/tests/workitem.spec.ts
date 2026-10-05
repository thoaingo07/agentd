import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { setEventConnectionFactory, type EventConnection } from '../ClientApps/shared/api/hub'
import type { AgentEvent, JobSummary } from '../ClientApps/shared/api/types'
import { label, sections, usageSamples } from '../ClientApps/dashboard/components/workitem/sections'
import ConversationTab from '../ClientApps/dashboard/components/workitem/ConversationTab.vue'
import PlanTab from '../ClientApps/dashboard/components/workitem/PlanTab.vue'
import TimelineTab from '../ClientApps/dashboard/components/workitem/TimelineTab.vue'
import { useConnectionStore } from '../ClientApps/dashboard/stores/connection'
import { useWorkItemsStore } from '../ClientApps/dashboard/stores/workItems'

const job = (id: number, state = 'Done', handoff = 'None'): JobSummary => ({
  id, workItemId: 5613, title: 'Refine', repo: 'sysmin', branch: null, state, phase: null, startedAt: '2026-10-03T16:00:00Z',
  elapsedSeconds: 600, prUrl: null, waitingSince: null, planStatus: 'Approved', handoff, fixRounds: 0, lastError: null, completedAt: null, pendingPermissions: 0,
})
const ev = (seq: number, jobId: number, type: string, payload: object = {}): AgentEvent => ({ seq, jobId, ts: '2026-10-03T16:00:00Z', type, payload })
const router = () => createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<div />' } }, { path: '/jobs/:id', name: 'job', component: { template: '<div />' } }] })

beforeEach(() => setActivePinia(createPinia()))
afterEach(() => vi.unstubAllGlobals())

describe('sections', () => {
  it('groups events per run and labels reworks and the hand-off', () => {
    const groups = sections([job(1), job(2), job(3, 'Done', 'Agreed')], [ev(1, 1, 'JobQueued'), ev(2, 2, 'JobQueued'), ev(3, 3, 'HandoffStarted'), ev(4, 1, 'x')])
    expect(groups.map((g) => [g.label, g.events.map((e) => e.seq)])).toEqual([
      ['Run 1', [1, 4]],
      ['Run 2 (rework)', [2]],
      ['Run 3 (rework) · hand-off', [3]],
    ])
    expect(label(0, job(1, 'Done', 'Requested'))).toBe('Run 1 · hand-off')
  })

  it('reads usage readings from rate-limit events', () => {
    expect(usageSamples([ev(1, 1, 'agent.rate_limit', { utilization: { five_hour: 0.4, seven_day: 0.1 } }), ev(2, 1, 'agent.text')])).toEqual([
      { at: '2026-10-03T16:00:00Z', fiveHour: 0.4, weekly: 0.1 },
    ])
  })
})

describe('TimelineTab', () => {
  it('shows a section per job with its steps, not the agent output', () => {
    const w = mount(TimelineTab, {
      props: { jobs: [job(1), job(2)], events: [ev(1, 1, 'PlanApproved', { by: 'tngo' }), ev(2, 1, 'agent.text', { text: 'secret thoughts' }), ev(3, 2, 'PullRequestCreated', { url: { value: 'https://x/pr/1' } })], hasMore: true },
      global: { plugins: [router()] },
    })
    expect(w.findAll('section').map((s) => s.attributes('aria-label'))).toEqual(['Run 1', 'Run 2 (rework)'])
    expect(w.text()).toContain('Plan approved')
    expect(w.text()).toContain('Pull request opened')
    expect(w.text()).not.toContain('secret thoughts')
    expect(w.findAll('button').some((b) => b.text() === 'Load more')).toBe(true)
  })
})

describe('ConversationTab', () => {
  it('shows both directions with delivery status', () => {
    const w = mount(ConversationTab, {
      props: {
        entries: [
          { at: '2026-10-03T16:00:00Z', jobId: 1, direction: 'out', kind: 'Question', text: '**Which** endpoint?', provider: 'discord', author: null, status: 'dead', error: '403 Missing Permissions' },
          { at: '2026-10-03T16:01:00Z', jobId: 1, direction: 'in', kind: 'reply', text: 'v2', provider: 'discord', author: 'tngo', status: null, error: null },
          { at: '2026-10-03T16:02:00Z', jobId: 1, direction: 'in', kind: 'command', text: '!status', provider: 'discord', author: 'tngo', status: null, error: null },
        ],
      },
    })
    expect(w.findAll('[data-direction]').map((d) => d.attributes('data-direction'))).toEqual(['out', 'in', 'in'])
    expect(w.find('strong').text()).toBe('Which')
    expect(w.get('.badge-error').text()).toBe('dead')
    expect(w.get('.badge-error').attributes('title')).toBe('403 Missing Permissions')
    expect(w.text()).toContain('tngo · discord')
    expect(w.find('textarea').exists()).toBe(false)
  })
})

describe('PlanTab', () => {
  it('puts the estimate next to the actual time and usage', () => {
    const detail = { estimate: { minutes: 20, usagePercent: 15, usageAtPlan: 0.1, submittedAt: '', approvedAt: null } } as never
    const w = mount(PlanTab, {
      props: { jobs: [job(1)], events: [ev(1, 1, 'agent.rate_limit', { utilization: { five_hour: 0.3, seven_day: 0.05 } })], details: new Map([[1, detail]]) },
    })
    expect(w.get('[data-time]').text()).toBe('10m 00s actual · ~20 min estimated')
    expect(w.get('[data-usage]').text()).toContain('peak 30% · ~15% estimated')
  })
})

describe('workItems store', () => {
  it('loads the story, subscribes the active job, and appends its live events', async () => {
    const subscribed: [string, number][] = []
    const fake: EventConnection = {
      start: async () => {}, stop: async () => {}, unsubscribe: async () => {},
      subscribe: async (s, a) => void subscribed.push([s, a]),
      onEvent: () => {}, onReconnecting: () => {}, onReconnected: () => {}, onClose: () => {},
    }
    setEventConnectionFactory(() => fake)
    vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
      const path = new URL(req.url).pathname
      const body = path === '/api/workitems/5613' ? { workItemId: 5613, title: 'Refine', repo: 'sysmin', jobs: [job(1), job(2, 'Running')], pullRequests: [], conversations: [], firstSeenAt: '2026-10-03T16:00:00Z', lastActivityAt: '2026-10-03T18:00:00Z' }
        : path.endsWith('/timeline') ? { events: [ev(5, 1, 'JobQueued'), ev(9, 2, 'JobStarted')], oldestSeq: 5, newestSeq: 9, hasMore: false }
          : path.endsWith('/conversation') ? [] : { job: job(1), estimate: null }
      return new Response(JSON.stringify(body))
    }))
    await useConnectionStore().start()
    const store = useWorkItemsStore()

    await store.open(5613)
    await flushPromises()
    expect(subscribed).toEqual([['2', 9]])

    useConnectionStore().route('2', ev(10, 2, 'agent.text', { text: 'working' }))
    useConnectionStore().route('2', ev(10, 2, 'agent.text', { text: 'duplicate' }))
    expect(store.events.map((e) => e.seq)).toEqual([5, 9, 10])
  })
})
