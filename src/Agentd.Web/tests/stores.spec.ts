import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { setEventConnectionFactory, type EventConnection } from '../ClientApps/shared/api/hub'
import type { AgentEvent, JobSummary } from '../ClientApps/shared/api/types'
import { useConnectionStore } from '../ClientApps/dashboard/stores/connection'
import { maxRendered, useEventsStore } from '../ClientApps/dashboard/stores/events'
import { refreshDelayMs, useJobsStore } from '../ClientApps/dashboard/stores/jobs'
import { useUiStore } from '../ClientApps/dashboard/stores/ui'

/** A fake hub: records subscriptions, lets the test push events and drop/restore the connection. */
class FakeHub implements EventConnection {
  subscribed: [string, number][] = []
  private event?: (stream: string, evt: AgentEvent) => void
  private reconnecting?: () => void
  private reconnected?: () => void
  start = vi.fn(async () => {})
  stop = vi.fn(async () => {})
  subscribe = vi.fn(async (stream: string, afterSeq: number) => void this.subscribed.push([stream, afterSeq]))
  unsubscribe = vi.fn(async () => {})
  onEvent(h: (stream: string, evt: AgentEvent) => void) { this.event = h }
  onReconnecting(h: () => void) { this.reconnecting = h }
  onReconnected(h: () => void) { this.reconnected = h }
  onClose() {}
  push(stream: string, evt: AgentEvent) { this.event!(stream, evt) }
  drop() { this.reconnecting!() }
  restore() { this.reconnected!() }
}

const ev = (seq: number, jobId = 7, type = 'agent.text'): AgentEvent => ({ seq, jobId, ts: '2026-10-04T00:00:00Z', type, payload: {} })
const job = (id: number, state: string, startedAt = '2026-10-04T10:00:00Z'): JobSummary => ({
  id, workItemId: 5600 + id, title: `job ${id}`, repo: 'sysmin', branch: null, state, phase: null, startedAt,
  elapsedSeconds: 1, prUrl: null, waitingSince: null, planStatus: 'NotRequired', handoff: 'None', fixRounds: 0, lastError: null,
})
function respond(routes: Record<string, unknown>) {
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const path = new URL(req.url).pathname + new URL(req.url).search
    const key = Object.keys(routes).find((k) => path.startsWith(k))
    const body = key ? routes[key] : { title: 'Not found' }
    const status = body instanceof Response ? body.status : key ? 200 : 404
    return body instanceof Response ? body : new Response(JSON.stringify(body), { status })
  }))
}

let hub: FakeHub
beforeEach(() => {
  setActivePinia(createPinia())
  hub = new FakeHub()
  setEventConnectionFactory(() => hub)
})
afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('events store', () => {
  it('drops duplicates and older seqs, and trims to the rendered limit', async () => {
    respond({ '/api/jobs/7/events': { events: [ev(1), ev(2)], oldestSeq: 1, newestSeq: 2, hasMore: false } })
    const events = useEventsStore()
    await events.open(7)

    for (const seq of [2, 1, 3, 3, 4]) events.append(ev(seq))
    expect(events.windows.get(7)!.events.map((e) => e.seq)).toEqual([1, 2, 3, 4])

    for (let seq = 5; seq <= maxRendered + 10; seq++) events.append(ev(seq))
    const w = events.windows.get(7)!
    expect(w.events).toHaveLength(maxRendered)
    expect(w.oldestSeq).toBe(11)
    expect(w.hasMore).toBe(true)
  })

  it('loads earlier events in order', async () => {
    respond({
      '/api/jobs/7/events?limit': { events: [ev(301), ev(302)], oldestSeq: 301, newestSeq: 302, hasMore: true },
      '/api/jobs/7/events?before=301': { events: [ev(299), ev(300)], oldestSeq: 299, newestSeq: 300, hasMore: false },
    })
    const events = useEventsStore()
    await events.open(7)

    await events.loadEarlier(7)

    expect(events.windows.get(7)!.events.map((e) => e.seq)).toEqual([299, 300, 301, 302])
    expect(events.windows.get(7)!.hasMore).toBe(false)
  })
})

describe('connection store', () => {
  it('resubscribes every stream from its own last seq after a reconnect', async () => {
    respond({ '/api/jobs/7/events': { events: [ev(10)], oldestSeq: 10, newestSeq: 10, hasMore: false } })
    const connection = useConnectionStore()
    await connection.start()
    await connection.subscribe('all', 100)
    await useEventsStore().open(7)
    hub.push('all', ev(120, 7, 'JobStarted'))
    hub.push('7', ev(11))
    hub.push('7', ev(12))

    hub.drop()
    expect(connection.status).toBe('reconnecting')
    hub.subscribed.length = 0
    hub.restore()
    await Promise.resolve()
    await Promise.resolve()

    expect(connection.status).toBe('live')
    expect(hub.subscribed).toEqual([['all', 120], ['7', 12]])
  })
})

describe('jobs store', () => {
  it('sorts waiting jobs first, then by start time', async () => {
    respond({ '/api/dashboard': { stats: {}, latestSeq: 5, activeJobs: [job(1, 'Running', '2026-10-04T09:00:00Z'), job(2, 'WaitingForHuman'), job(3, 'Running', '2026-10-04T08:00:00Z')] } })
    const jobs = useJobsStore()

    expect(await jobs.load()).toBe(5)

    expect(jobs.active.map((j) => j.id)).toEqual([2, 3, 1])
    expect(jobs.waitingCount).toBe(1)
  })

  it('re-reads a job once for a burst of its events', async () => {
    vi.useFakeTimers()
    respond({ '/api/jobs/7': { job: job(7, 'Running'), attempt: 1, resumeCount: 0, pendingMessages: 0, estimate: null, lastActivity: null, lastActivityAt: null, usage: null, conversations: [] } })
    const jobs = useJobsStore()

    for (const seq of [1, 2, 3]) jobs.apply(ev(seq, 7, 'JobStarted'))
    await vi.advanceTimersByTimeAsync(refreshDelayMs + 1)

    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(1)
    expect(jobs.byId.get(7)?.state).toBe('Running')
  })

  it('an optimistic cancel rolls back on 409 and shows a toast', async () => {
    respond({
      '/api/dashboard': { stats: {}, latestSeq: 0, activeJobs: [job(7, 'Running')] },
      '/bff/antiforgery': { token: 't' },
      '/api/jobs/7/cancel': new Response(JSON.stringify({ code: 'invalid_transition', detail: 'Cannot cancel from state Done.' }), { status: 409 }),
    })
    const jobs = useJobsStore()
    await jobs.load()

    const cancelling = jobs.cancel(7)
    expect(jobs.byId.get(7)?.state).toBe('Cancelled')
    expect(jobs.pending.has(7)).toBe(true)
    await cancelling

    expect(jobs.byId.get(7)?.state).toBe('Running')
    expect(jobs.pending.has(7)).toBe(false)
    expect(useUiStore().toasts.at(-1)).toMatchObject({ kind: 'error', message: 'Cannot cancel from state Done.' })
  })
})
