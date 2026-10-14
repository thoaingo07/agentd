import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, get, send } from '../../shared/api/http'
import type { AgentEvent, InboxItem, IncidentDetail, IncidentSummary } from '../../shared/api/types'

/** Debounce for re-reading after an incident's Inbox messages (an answer and its status change arrive together). */
export const incidentRefreshDelayMs = 400
/** The thread's words, so the page does exactly what the 🚨 thread's buttons and replies do. */
export const incidentActions = { fix: '🛠 Fix it', ignore: '🙈 Ignore', reopen: 'reopen', investigate: 'investigate again' } as const

/**
 * Incidents (docs/architect/incident-monitor.md §4): what the monitor found in Application Insights. Actions and questions
 * are Inbox replies, like the 🚨 thread's; an `inbox.added` event for an incident re-reads the list and the open one.
 */
export const useIncidentsStore = defineStore('incidents', () => {
  const list = ref<IncidentSummary[] | null>(null)
  const current = ref<IncidentDetail | null>(null)
  const currentId = ref<number | null>(null)
  const missing = ref(false)
  /** The open incident's thread, oldest first: the alert, the investigation, answers. */
  const thread = ref<InboxItem[]>([])
  let timer: ReturnType<typeof setTimeout> | undefined
  const touched = new Set<number>()

  async function load(): Promise<void> {
    list.value = await get<IncidentSummary[]>('/api/incidents')
  }

  async function open(id: number): Promise<void> {
    if (currentId.value !== id) {
      current.value = null
      thread.value = []
    }
    currentId.value = id
    missing.value = false
    try {
      const [incident, items] = await Promise.all([get<IncidentDetail>(`/api/incidents/${id}`), get<InboxItem[]>(`/api/inbox/incident/${id}`)])
      if (currentId.value !== id) return
      current.value = incident
      thread.value = [...items].reverse()
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) missing.value = true
      else throw err
    }
  }

  /** One of {@link incidentActions}, or a question for the investigating agent; returns the outcome. */
  async function reply(text: string): Promise<string> {
    const id = currentId.value
    if (id === null) return 'none'
    const { outcome } = await send<{ outcome: string }>('POST', `/api/inbox/incident/${id}/reply`, { text })
    await open(id)
    return outcome
  }

  function close(): void {
    currentId.value = null
    current.value = null
    thread.value = []
  }

  function apply(evt: AgentEvent): void {
    const payload = evt.payload as Record<string, unknown>
    if (evt.type !== 'inbox.added' || payload.source !== 'incident') return
    touched.add(Number(payload.sourceId))
    if (timer) return
    timer = setTimeout(() => {
      timer = undefined
      if (list.value) void load().catch(() => {})
      if (currentId.value !== null && touched.has(currentId.value)) void open(currentId.value).catch(() => {})
      touched.clear()
    }, incidentRefreshDelayMs)
  }

  return { list, current, currentId, missing, thread, load, open, reply, close, apply }
})
