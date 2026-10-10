import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { setEventConnectionFactory, type EventConnection } from '../ClientApps/shared/api/hub'
import type { AgentEvent, JobSummary } from '../ClientApps/shared/api/types'
import { useConnectionStore } from '../ClientApps/dashboard/stores/connection'
import { maxRendered, useEventsStore } from '../ClientApps/dashboard/stores/events'
import { refreshDelayMs, useJobsStore } from '../ClientApps/dashboard/stores/jobs'
import { usePermissionsStore } from '../ClientApps/dashboard/stores/permissions'
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
  elapsedSeconds: 1, prUrl: null, waitingSince: null, planStatus: 'NotRequired', handoff: 'None', fixRounds: 0, lastError: null, completedAt: null, pendingPermissions: 0,
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
  it('opens on the newest page, applies a burst of live events at once, drops duplicates and trims', async () => {
    respond({ '/api/jobs/7/events?before=9007199254740991&limit=100': { events: [ev(1), ev(2)], oldestSeq: 1, newestSeq: 2, hasMore: false } })
    const connection = useConnectionStore()
    await connection.start()
    const events = useEventsStore()
    await events.open(7)
    expect(hub.subscribed).toEqual([['7', 2]])
    const shown = events.windows.get(7)!.events

    for (const seq of [2, 1, 3, 3, 4]) events.append(ev(seq))
    expect(events.windows.get(7)!.events).toBe(shown)   // nothing renders until the frame
    events.flush()
    expect(events.windows.get(7)!.events.map((e) => e.seq)).toEqual([1, 2, 3, 4])

    for (let seq = 5; seq <= maxRendered + 10; seq++) events.append(ev(seq))
    events.flush()
    const w = events.windows.get(7)!
    expect(w.events).toHaveLength(maxRendered)
    expect(w.oldestSeq).toBe(11)
    expect(w.hasMore).toBe(true)
  })

  it('a finished run is read page by page without streaming, and a run that finishes stops streaming', async () => {
    respond({
      '/api/jobs/7/events?before=9007199254740991': { events: [ev(301), ev(302)], oldestSeq: 301, newestSeq: 302, hasMore: true },
      '/api/jobs/7/events?before=301&limit=100': { events: [ev(299), ev(300)], oldestSeq: 299, newestSeq: 300, hasMore: false },
      '/api/jobs/8/events': { events: [ev(5, 8)], oldestSeq: 5, newestSeq: 5, hasMore: false },
    })
    const connection = useConnectionStore()
    await connection.start()
    const events = useEventsStore()
    await events.open(7, false)

    await events.loadEarlier(7)
    events.append(ev(303))
    events.flush()

    expect(hub.subscribed).toEqual([])
    expect(events.windows.get(7)!.events.map((e) => e.seq)).toEqual([299, 300, 301, 302])
    expect(events.windows.get(7)!.hasMore).toBe(false)

    await events.open(8)
    events.settle(8)
    events.close(8)
    expect(hub.unsubscribe).toHaveBeenCalledTimes(1)
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
    useEventsStore().flush()

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

describe('shared streams', () => {
  it('a stream shared by two consumers replays from the earlier seq and stays until the last one leaves', async () => {
    respond({ '/api/jobs/7/events': { events: [ev(10)], oldestSeq: 10, newestSeq: 10, hasMore: false } })
    const connection = useConnectionStore()
    await connection.start()
    await connection.subscribe('7', 4)   // the work item's timeline, behind
    await useEventsStore().open(7)       // the run's transcript, at 10: no new subscription needed
    expect(hub.subscribed).toEqual([['7', 4]])

    useEventsStore().close(7)
    expect(hub.unsubscribe).not.toHaveBeenCalled()
    await useEventsStore().open(7)       // back to the run within the keep window: counted again
    useEventsStore().close(7)
    await connection.unsubscribe('7')
    expect(hub.unsubscribe).toHaveBeenCalledTimes(1)
    expect(connection.subscriptions.has('7')).toBe(false)
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
    respond({ '/api/jobs/7': { job: job(7, 'Running'), attempt: 1, resumeCount: 0, pendingMessages: 0, estimate: null, lastActivity: null, lastActivityAt: null, usage: null, conversations: [], permissions: [] } })
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

describe('permissions', () => {
  const detail = (permissions: unknown[]) => ({ job: job(7, 'Running'), attempt: 1, resumeCount: 0, pendingMessages: 0, estimate: null, lastActivity: null, lastActivityAt: null, usage: null, conversations: [], permissions })
  const request = { id: 3, tool: 'Bash', summary: 'npm install', ruleKeys: ['Bash(npm install:*)'], requestedAt: '2026-10-05T10:00:00Z' }

  it('answering posts the choice, then re-reads the job', async () => {
    respond({ '/bff/antiforgery': { token: 't' }, '/api/jobs/7/permissions/3': new Response(null, { status: 204 }), '/api/jobs/7': detail([]) })
    const jobs = useJobsStore()

    await jobs.answerPermission(7, 3, 'job')

    const post = vi.mocked(fetch).mock.calls.map(([r]) => r as Request).find((r) => r.method === 'POST')!
    expect(new URL(post.url).pathname).toBe('/api/jobs/7/permissions/3')
    expect(await post.json()).toEqual({ choice: 'job' })
    expect(jobs.details.get(7)?.permissions).toEqual([])
    expect(jobs.pending.has(7)).toBe(false)
  })

  it('an answer that lost the race is an info toast, and the refresh shows the open requests', async () => {
    respond({
      '/bff/antiforgery': { token: 't' },
      '/api/jobs/7/permissions/3': new Response(JSON.stringify({ code: 'already_decided', detail: 'Permission request 3 was already answered or has expired.' }), { status: 409 }),
      '/api/jobs/7': detail([{ ...request, id: 4 }]),
    })
    const jobs = useJobsStore()

    await jobs.answerPermission(7, 3, 'once')

    expect(useUiStore().toasts.at(-1)).toMatchObject({ kind: 'info', message: 'Permission request 3 was already answered or has expired.' })
    expect(jobs.details.get(7)?.permissions.map((p) => p.id)).toEqual([4])
  })

  it('revoking a rule removes it, also when it was already gone', async () => {
    const rule = (id: number) => ({ id, repo: 'sysmin', jobId: null, ruleKey: `Bash(r${id}:*)`, createdBy: 'tngo', createdAt: '2026-10-05T10:00:00Z' })
    respond({
      '/bff/antiforgery': { token: 't' },
      '/api/permissions/rules/1': new Response(null, { status: 204 }),
      '/api/permissions/rules/2': new Response(JSON.stringify({ code: 'not_found' }), { status: 404 }),
      '/api/permissions/rules': [rule(1), rule(2), rule(3)],
    })
    const permissions = usePermissionsStore()
    await permissions.load()

    await permissions.revoke(1)
    await permissions.revoke(2)

    expect(permissions.rules.map((r) => r.id)).toEqual([3])
    expect(useUiStore().toasts).toEqual([])
  })
})
