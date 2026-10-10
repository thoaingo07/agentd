import { defineStore } from 'pinia'
import { computed, reactive } from 'vue'
import { ApiError, get, send } from '../../shared/api/http'
import type { AgentEvent, Dashboard, JobDetail, JobState, JobSummary, PermissionChoice } from '../../shared/api/types'
import { useUiStore } from './ui'

const finalStates: JobState[] = ['Done', 'Failed', 'Cancelled']
const order: JobState[] = ['WaitingForHuman', 'Running', 'Preparing', 'Queued', 'Publishing', 'InReview']

/** Debounce for re-reading a job after its events (a burst of events → one request). */
export const refreshDelayMs = 250
/** What the server did with a message, when it's not just "the agent has it". */
export const outcomeNotes: Record<string, string> = {
  fix_round: 'Fix round started: the agent addresses your message on the PR and pushes.',
  follow_up: 'The job’s agent answers under Conversation (talk only: nothing is pushed).',
  close_out: 'Done: the job’s chat threads are cleaned up as you chose.',
  handoff_declined: 'No hand-off: the job is done.',
}

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
  async function act(id: number, action: 'cancel' | 'retry' | 'pause' | 'resume', optimistic: JobState): Promise<void> {
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

  /** Answers one permission request. 409: someone answered first (chat or another tab); the refresh shows theirs. */
  async function answerPermission(id: number, requestId: number, choice: PermissionChoice): Promise<void> {
    pending.add(id)
    try {
      await send('POST', `/api/jobs/${id}/permissions/${requestId}`, { choice })
    } catch (err) {
      useUiStore().toast(err instanceof ApiError ? err.message : `Couldn't answer request ${requestId}.`, err instanceof ApiError && err.status === 409 ? 'info' : 'error')
    } finally {
      pending.delete(id)
      await refresh(id).catch(() => {})
    }
  }

  /**
   * A message to a job, routed like a reply in its chat thread: an answer, the next turn, a fix round in review, the
   * close-out answer, or after the merge a follow-up (answered under Conversation). Null when it wasn't taken (toasted).
   */
  async function message(id: number, text: string): Promise<string | null> {
    try {
      const { outcome } = await send<{ outcome: string }>('POST', `/api/jobs/${id}/messages`, { text })
      const note = outcomeNotes[outcome]
      if (note) useUiStore().toast(note, 'info')
      void refresh(id).catch(() => {})
      return outcome
    } catch (err) {
      useUiStore().toast(err instanceof ApiError ? err.message : 'The message wasn’t sent.', 'error')
      return null
    }
  }

  /** The knowledge hand-off (like !handoff): the agent proposes what to add to AGENTS.md, in the job's thread. */
  async function handoff(id: number): Promise<void> {
    pending.add(id)
    try {
      await send('POST', `/api/jobs/${id}/handoff`)
      useUiStore().toast('Hand-off started: the agent proposes what to keep, then asks you here.', 'info')
    } catch (err) {
      useUiStore().toast(err instanceof ApiError ? err.message : `Couldn't start the hand-off for job #${id}.`, 'error')
    } finally {
      pending.delete(id)
      await refresh(id).catch(() => {})
    }
  }

  const cancel = (id: number) => act(id, 'cancel', 'Cancelled')
  const retry = (id: number) => act(id, 'retry', 'Queued')
  const pause = (id: number) => act(id, 'pause', 'Paused')
  const resume = (id: number) => act(id, 'resume', 'Queued')

  return { byId, details, pending, active, waitingCount, stats, load, refresh, apply, cancel, retry, pause, resume, answerPermission, message, handoff }
})
