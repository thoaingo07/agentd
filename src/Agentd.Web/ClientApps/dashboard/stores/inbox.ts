import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { get, send } from '../../shared/api/http'
import type { AgentEvent, InboxCounts, InboxItem, InboxThread } from '../../shared/api/types'

/** Debounce for re-reading after Inbox and job events (a burst is one read). */
export const inboxRefreshDelayMs = 400

export type InboxView = 'all' | 'mine'
export interface ThreadKey { source: string; sourceId: number }

/**
 * The Inbox (docs/architect/inbox.md): agentd's messages to people, one thread per source, waiting on you first. Kept
 * fresh from the "all" stream: `inbox.added` (a new message) and job events (a job that starts or stops waiting).
 */
export const useInboxStore = defineStore('inbox', () => {
  const threads = ref<InboxThread[] | null>(null)
  const counts = ref<InboxCounts>({ waiting: 0, unread: 0 })
  const view = ref<InboxView>('all')
  const open = ref<ThreadKey | null>(null)
  const items = ref<InboxItem[]>([])
  let timer: ReturnType<typeof setTimeout> | undefined
  let listening = false

  /**
   * Waiting on you (not marked read since agentd last wrote), new (unread), and the rest: the server's order, grouped. The
   * open thread keeps the group it had when you opened it, so reading it doesn't move it from under you.
   */
  const pinned = ref<{ key: string; group: 'waiting' | 'new' | 'earlier' } | null>(null)
  const keyOf = (t: { source: string; sourceId: number }) => `${t.source}:${t.sourceId}`
  const groupOf = (t: InboxThread) => (pinned.value?.key === keyOf(t) ? pinned.value.group : t.waiting && t.unread > 0 ? 'waiting' : t.unread > 0 ? 'new' : 'earlier')
  const sections = computed(() => {
    const all = threads.value ?? []
    const waiting = all.filter((t) => groupOf(t) === 'waiting')
    const fresh = all.filter((t) => groupOf(t) === 'new')
    const earlier = all.filter((t) => groupOf(t) === 'earlier')
    return [
      { id: 'waiting', label: 'Waiting on you', threads: waiting },
      { id: 'new', label: 'New', threads: fresh },
      { id: 'earlier', label: 'Earlier', threads: earlier },
    ].filter((s) => s.threads.length > 0)
  })
  const current = computed(() => threads.value?.find((t) => open.value && t.source === open.value.source && t.sourceId === open.value.sourceId) ?? null)

  async function load(): Promise<void> {
    const page = await get<{ threads: InboxThread[]; counts: InboxCounts }>(`/api/inbox?view=${view.value}`)
    threads.value = page.threads
    counts.value = page.counts
  }

  async function loadCounts(): Promise<void> {
    counts.value = await get<InboxCounts>('/api/inbox/counts')
  }

  /** Opens a thread: its messages, oldest first, and marks it read up to its latest. */
  async function openThread(key: ThreadKey): Promise<void> {
    open.value = key
    const t = threads.value?.find((x) => x.source === key.source && x.sourceId === key.sourceId)
    pinned.value = t ? { key: keyOf(t), group: t.waiting && t.unread > 0 ? 'waiting' : t.unread > 0 ? 'new' : 'earlier' } : null
    const page = await get<InboxItem[]>(`/api/inbox/${key.source}/${key.sourceId}`)
    if (open.value?.source !== key.source || open.value.sourceId !== key.sourceId) return
    items.value = [...page].reverse()
    const latest = page[0]?.id
    if (latest) await markRead(key, latest)
  }

  async function markRead(key: ThreadKey, until: number): Promise<void> {
    await send('POST', `/api/inbox/${key.source}/${key.sourceId}/read`, { until })
    const t = threads.value?.find((x) => x.source === key.source && x.sourceId === key.sourceId)
    if (t) t.unread = 0
    await loadCounts()
  }

  /** An answer, to the thread's source (a button's label, or text as in its chat thread or page). */
  async function reply(key: ThreadKey, text: string): Promise<string> {
    const { outcome } = await send<{ outcome: string }>('POST', `/api/inbox/${key.source}/${key.sourceId}/reply`, { text })
    return outcome
  }

  function close(): void {
    open.value = null
    pinned.value = null
    items.value = []
  }

  /** Re-reads after events once per burst: the list when it's shown, the open thread, and always the nav's counts. */
  function apply(evt: AgentEvent): void {
    if (!(evt.type.startsWith('inbox.') || evt.type.startsWith('job.') || evt.type.startsWith('idea.') || evt.type.startsWith('review.') || evt.type.startsWith('watch.'))) return
    clearTimeout(timer)
    timer = setTimeout(() => {
      if (listening) void load().catch(() => {})
      else void loadCounts().catch(() => {})
      if (open.value) {
        const key = open.value
        void get<InboxItem[]>(`/api/inbox/${key.source}/${key.sourceId}`).then((page) => {
          if (open.value?.source === key.source && open.value.sourceId === key.sourceId) items.value = [...page].reverse()
        }).catch(() => {})
      }
    }, inboxRefreshDelayMs)
  }

  /** The Inbox page is shown: events re-read the whole list, not just the counts. Returns the stop function. */
  function watchList(): () => void {
    listening = true
    return () => (listening = false)
  }

  return { threads, counts, view, open, items, sections, current, load, loadCounts, openThread, markRead, reply, close, apply, watchList }
})
