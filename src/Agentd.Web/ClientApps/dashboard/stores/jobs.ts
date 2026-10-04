import { defineStore } from 'pinia'
import { computed, reactive } from 'vue'
import { ApiError, get, send } from '../../shared/api/http'
import type { AgentEvent, Dashboard, JobDetail, JobState, JobSummary } from '../../shared/api/types'
import { useUiStore } from './ui'

const finalStates: JobState[] = ['Done', 'Failed', 'Cancelled']
const order: JobState[] = ['WaitingForHuman', 'Running', 'Preparing', 'Queued', 'Publishing', 'InReview']

/** Debounce for re-reading a job after its events (a burst of events → one request). */
export const refreshDelayMs = 250

/**
 * Jobs as the dashboard shows them. Loaded from /api/dashboard; kept fresh from the "all" stream: a
 * summary event for a job re-reads that job (/api/jobs/{id}), so the store never has to know every
 * event's payload shape.
 */
export const useJobsStore = defineStore('jobs', () => {
  const byId = reactive(new Map<number, JobSummary>())
  const details = reactive(new Map<number, JobDetail>())
  const pending = reactive(new Set<number>())
  const timers = new Map<number, ReturnType<typeof setTimeout>>()

  const active = computed(() =>
    [...byId.values()]
      .filter((j) => !finalStates.includes(j.state as JobState))
      .sort((a, b) => order.indexOf(a.state as JobState) - order.indexOf(b.state as JobState) || a.startedAt.localeCompare(b.startedAt)),
  )
  const waitingCount = computed(() => active.value.filter((j) => j.state === 'WaitingForHuman').length)
  const stats = computed(() => {
    const counts: Partial<Record<JobState, number>> = {}
    for (const j of active.value) counts[j.state as JobState] = (counts[j.state as JobState] ?? 0) + 1
    return counts
  })

  /** Loads the snapshot; returns its stream position, where the "all" subscription should start. */
  async function load(): Promise<number> {
    const dashboard = await get<Dashboard>('/api/dashboard')
    for (const id of [...byId.keys()]) if (!dashboard.activeJobs.some((j) => j.id === id)) byId.delete(id)
    for (const job of dashboard.activeJobs) byId.set(job.id, job)
    return dashboard.latestSeq
  }

  async function refresh(id: number): Promise<void> {
    try {
      const detail = await get<JobDetail>(`/api/jobs/${id}`)
      details.set(id, detail)
      byId.set(id, detail.job)
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) byId.delete(id)
      else throw err
    }
  }

  /** A live event: schedule one re-read of its job. */
  function apply(evt: AgentEvent): void {
    const id = evt.jobId
    if (id == null || timers.has(id)) return
    timers.set(
      id,
      setTimeout(() => {
        timers.delete(id)
        void refresh(id).catch(() => {})
      }, refreshDelayMs),
    )
  }

  /** Optimistic: shows the new state at once, rolls back (with a toast) if the server says no. */
  async function act(id: number, action: 'cancel' | 'retry', optimistic: JobState): Promise<void> {
    const before = byId.get(id)
    pending.add(id)
    if (before) byId.set(id, { ...before, state: optimistic })
    try {
      await send('POST', `/api/jobs/${id}/${action}`)
    } catch (err) {
      if (before) byId.set(id, before)
      useUiStore().toast(err instanceof ApiError ? err.message : `Couldn't ${action} job #${id}.`, 'error')
    } finally {
      pending.delete(id)
    }
  }

  const cancel = (id: number) => act(id, 'cancel', 'Cancelled')
  const retry = (id: number) => act(id, 'retry', 'Queued')

  return { byId, details, pending, active, waitingCount, stats, load, refresh, apply, cancel, retry }
})
