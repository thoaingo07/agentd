import { describe, expect, it } from 'vitest'
import { isJobState, jobStates } from '../ClientApps/shared/api/types'
import type { Dashboard, Diff, EventPage } from '../ClientApps/shared/api/types'

// The assignments below are the real check: a server change that breaks these shapes fails `vue-tsc`.
const dashboard: Dashboard = {
  stats: { WaitingForHuman: 1 },
  activeJobs: [
    {
      id: 7,
      workItemId: 5613,
      title: 'Refine the AGENTS.md',
      repo: 'sysmin',
      branch: 'ai/5613-refine',
      state: 'WaitingForHuman',
      phase: 'plan',
      startedAt: '2026-10-03T16:35:00Z',
      elapsedSeconds: 720,
      prUrl: null,
      waitingSince: '2026-10-03T16:40:00Z',
      planStatus: 'Pending',
      handoff: 'None',
      fixRounds: 0,
      lastError: null,
    },
  ],
}

const page: EventPage = {
  events: [{ seq: 98, jobId: 7, ts: '1970-01-01T00:00:00Z', type: 'phase.set', payload: { phase: 'plan' } }],
  oldestSeq: 98,
  newestSeq: 98,
  hasMore: true,
}

const diff: Diff = { baseRef: 'origin/develop', headRef: 'ai/5613-refine', files: ['AGENTS.md'], unifiedDiff: null, truncated: true }

describe('BFF contract types', () => {
  it('accepts the server shapes', () => {
    expect(dashboard.activeJobs[0]?.workItemId).toBe(5613)
    expect(page.hasMore).toBe(true)
    expect(diff.unifiedDiff).toBeNull()
  })

  it('knows every job state', () => {
    expect(jobStates).toHaveLength(9)
    expect(dashboard.activeJobs.every((j) => isJobState(j.state))).toBe(true)
    expect(isJobState('Sleeping')).toBe(false)
  })
})
