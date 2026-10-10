import { defineStore } from 'pinia'
import { reactive } from 'vue'
import { get } from '../../shared/api/http'
import type { AgentEvent, EventPage } from '../../shared/api/types'
import { useConnectionStore } from './connection'

export interface JobWindow {
  events: AgentEvent[]
  oldestSeq: number | null
  newestSeq: number | null
  hasMore: boolean
  lastViewedAt: number
}

/** Rendered events kept per open job; older ones are dropped and reloadable ("load earlier"). */
export const maxRendered = 2000
export const closeAfterMs = 5 * 60 * 1000

/** Full event streams of the jobs being viewed (Session view), deduplicated by seq. */
export const useEventsStore = defineStore('events', () => {
  const windows = reactive(new Map<number, JobWindow>())
  const closing = new Map<number, ReturnType<typeof setTimeout>>()

  async function open(jobId: number): Promise<void> {
    clearTimeout(closing.get(jobId))
    closing.delete(jobId)
    const existing = windows.get(jobId)
    if (existing) {
      // Kept since it was closed (switching runs, coming back): stream again from where it stopped.
      existing.lastViewedAt = Date.now()
      await useConnectionStore().subscribe(String(jobId), existing.newestSeq ?? 0)
      return
    }

    const page = await get<EventPage>(`/api/jobs/${jobId}/events?limit=500`)
    windows.set(jobId, { events: page.events, oldestSeq: page.oldestSeq, newestSeq: page.newestSeq, hasMore: false, lastViewedAt: Date.now() })
    // Paged from the start: anything before the first page is "earlier". Live events continue after newestSeq.
    windows.get(jobId)!.hasMore = page.hasMore && page.oldestSeq !== null && page.oldestSeq > 1
    await useConnectionStore().subscribe(String(jobId), page.newestSeq ?? 0)
  }

  function append(evt: AgentEvent): void {
    const w = evt.jobId == null ? undefined : windows.get(evt.jobId)
    if (!w || (w.newestSeq !== null && evt.seq <= w.newestSeq)) return
    w.events.push(evt)
    w.newestSeq = evt.seq
    w.oldestSeq ??= evt.seq
    if (w.events.length > maxRendered) {
      w.events.splice(0, w.events.length - maxRendered)
      w.oldestSeq = w.events[0]!.seq
      w.hasMore = true
    }
  }

  async function loadEarlier(jobId: number): Promise<void> {
    const w = windows.get(jobId)
    if (!w || w.oldestSeq === null || !w.hasMore) return
    const page = await get<EventPage>(`/api/jobs/${jobId}/events?before=${w.oldestSeq}&limit=200`)
    w.events.unshift(...page.events)
    w.oldestSeq = page.oldestSeq ?? w.oldestSeq
    w.hasMore = page.hasMore
  }

  /** Unsubscribes now; keeps the window for a few minutes in case the user comes back. */
  function close(jobId: number): void {
    void useConnectionStore().unsubscribe(String(jobId))
    clearTimeout(closing.get(jobId))
    closing.set(
      jobId,
      setTimeout(() => {
        closing.delete(jobId)
        windows.delete(jobId)
      }, closeAfterMs),
    )
  }

  return { windows, open, append, loadEarlier, close }
})
