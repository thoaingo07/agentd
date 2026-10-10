import { defineStore } from 'pinia'
import { markRaw, reactive } from 'vue'
import { get } from '../../shared/api/http'
import type { AgentEvent, EventPage } from '../../shared/api/types'
import { useConnectionStore } from './connection'

export interface JobWindow {
  /** Not deeply reactive: replaced (never mutated) on every change, so a burst of events renders once. */
  events: readonly AgentEvent[]
  oldestSeq: number | null
  newestSeq: number | null
  hasMore: boolean
  /** Streaming new events (the run is active); a finished run's window only grows backwards. */
  live: boolean
  lastViewedAt: number
}

/** Rendered events kept per open job; older ones are dropped and reloadable (scrolling up). */
export const maxRendered = 1000
/** The newest events a transcript opens with, and each earlier page. */
export const pageSize = 100
export const closeAfterMs = 5 * 60 * 1000
/** `before` past every seq: the newest page. */
const newestPage = Number.MAX_SAFE_INTEGER

/**
 * One run's transcript (the work item page's Transcript tab), deduplicated by seq. It opens on the newest page; an
 * active run then streams what comes next, and scrolling up pages back. Live events are applied once per frame.
 */
export const useEventsStore = defineStore('events', () => {
  const windows = reactive(new Map<number, JobWindow>())
  const closing = new Map<number, ReturnType<typeof setTimeout>>()
  const queued = new Map<number, AgentEvent[]>()
  let flushing = false

  async function open(jobId: number, live = true): Promise<void> {
    clearTimeout(closing.get(jobId))
    closing.delete(jobId)
    const existing = windows.get(jobId)
    if (existing) {
      // Kept since it was closed (switching runs, coming back): stream again from where it stopped.
      existing.lastViewedAt = Date.now()
      existing.live = live
      if (live) await useConnectionStore().subscribe(String(jobId), existing.newestSeq ?? 0)
      return
    }

    const page = await get<EventPage>(`/api/jobs/${jobId}/events?before=${newestPage}&limit=${pageSize}`)
    windows.set(jobId, {
      events: markRaw(page.events.map(markRaw)),
      oldestSeq: page.oldestSeq,
      newestSeq: page.newestSeq,
      hasMore: page.hasMore,
      live,
      lastViewedAt: Date.now(),
    })
    if (live) await useConnectionStore().subscribe(String(jobId), page.newestSeq ?? 0)
  }

  /** A live event (routed by the connection store): queued, then applied with the rest of its frame. */
  function append(evt: AgentEvent): void {
    if (evt.jobId == null || !windows.get(evt.jobId)?.live) return
    const q = queued.get(evt.jobId) ?? []
    q.push(evt)
    queued.set(evt.jobId, q)
    if (!flushing) {
      flushing = true
      nextFrame(flush)
    }
  }

  function flush(): void {
    flushing = false
    for (const [jobId, q] of queued) {
      const w = windows.get(jobId)
      if (!w) continue
      const fresh: AgentEvent[] = []
      for (const evt of q) {
        if (w.newestSeq !== null && evt.seq <= w.newestSeq) continue
        fresh.push(markRaw(evt))
        w.newestSeq = evt.seq
      }

      if (!fresh.length) continue
      let next = [...w.events, ...fresh]
      if (next.length > maxRendered) {
        next = next.slice(next.length - maxRendered)
        w.hasMore = true
      }

      w.oldestSeq = next[0]!.seq
      w.events = markRaw(next)
    }

    queued.clear()
  }

  async function loadEarlier(jobId: number): Promise<void> {
    const w = windows.get(jobId)
    if (!w || w.oldestSeq === null || !w.hasMore) return
    const page = await get<EventPage>(`/api/jobs/${jobId}/events?before=${w.oldestSeq}&limit=${pageSize}`)
    w.events = markRaw([...page.events.map(markRaw), ...w.events])
    w.oldestSeq = page.oldestSeq ?? w.oldestSeq
    w.hasMore = page.hasMore
  }

  /** The run finished: nothing more comes, so stop streaming but keep what's shown. */
  function settle(jobId: number): void {
    const w = windows.get(jobId)
    if (!w?.live) return
    w.live = false
    void useConnectionStore().unsubscribe(String(jobId))
  }

  /** Unsubscribes now; keeps the window for a few minutes in case the user comes back. */
  function close(jobId: number): void {
    const w = windows.get(jobId)
    if (w?.live) {
      w.live = false
      void useConnectionStore().unsubscribe(String(jobId))
    }

    clearTimeout(closing.get(jobId))
    closing.set(
      jobId,
      setTimeout(() => {
        closing.delete(jobId)
        windows.delete(jobId)
      }, closeAfterMs),
    )
  }

  return { windows, open, append, flush, loadEarlier, settle, close }
})

function nextFrame(fn: () => void): void {
  if (typeof requestAnimationFrame === 'function' && typeof document !== 'undefined' && document.visibilityState !== 'hidden') requestAnimationFrame(fn)
  else setTimeout(fn, 16)
}
