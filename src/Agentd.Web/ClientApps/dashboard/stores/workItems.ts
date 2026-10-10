import { defineStore } from 'pinia'
import { ref } from 'vue'
import { get } from '../../shared/api/http'
import type { AgentEvent, ConversationEntry, EventPage, JobDetail, WorkItem } from '../../shared/api/types'
import { useConnectionStore } from './connection'

const finalStates = ['Done', 'Failed', 'Cancelled']
export const timelinePage = 500
/** New chat lines don't arrive as events; re-read the conversation shortly after activity. */
export const conversationRefreshMs = 2000

/** One work item across all its jobs (/workitems/:id), kept live while one of its jobs is active. */
export const useWorkItemsStore = defineStore('workItems', () => {
  const id = ref<number | null>(null)
  const summary = ref<WorkItem | null>(null)
  const events = ref<AgentEvent[]>([])
  const hasMore = ref(false)
  const conversation = ref<ConversationEntry[]>([])
  const details = ref(new Map<number, JobDetail>())
  const error = ref<string | null>(null)
  const live = new Set<string>()
  let refresh: ReturnType<typeof setTimeout> | undefined

  const newest = () => events.value.at(-1)?.seq ?? 0

  async function open(workItemId: number): Promise<void> {
    close()
    id.value = workItemId
    summary.value = null
    events.value = []
    conversation.value = []
    details.value = new Map()
    error.value = null
    try {
      summary.value = await get<WorkItem>(`/api/workitems/${workItemId}`)
    } catch {
      error.value = `agentd has no jobs for work item ${workItemId}.`
      return
    }
    await Promise.all([loadMore(), loadConversation(), ...summary.value.jobs.map(async (j) => details.value.set(j.id, await get<JobDetail>(`/api/jobs/${j.id}`)))])
  }

  /** The story is read from the beginning; this fetches the next page. */
  async function loadMore(): Promise<void> {
    if (id.value === null) return
    const page = await get<EventPage>(`/api/workitems/${id.value}/timeline?after=${newest()}&limit=${timelinePage}`)
    events.value.push(...page.events)
    hasMore.value = page.hasMore
    if (!page.hasMore) await goLive()
  }

  /**
   * Streams the active jobs once the timeline has reached the end: the hub only replays what's newer. (Subscribing
   * from a page in the middle would replay the rest of a long run one event at a time.)
   */
  async function goLive(): Promise<void> {
    const connection = useConnectionStore()
    for (const job of summary.value?.jobs.filter((j) => !finalStates.includes(j.state) && !live.has(String(j.id))) ?? []) {
      live.add(String(job.id))
      await connection.subscribe(String(job.id), newest())
    }
  }

  async function loadConversation(): Promise<void> {
    if (id.value !== null) conversation.value = await get<ConversationEntry[]>(`/api/workitems/${id.value}/conversation`)
  }

  /** A live event of one of its jobs (routed by the connection store). */
  function append(evt: AgentEvent): void {
    if (!summary.value || hasMore.value || !summary.value.jobs.some((j) => j.id === evt.jobId) || evt.seq <= newest()) return
    events.value.push(evt)
    if (!evt.type.startsWith('agent.')) {
      clearTimeout(refresh)
      refresh = setTimeout(() => void loadConversation().catch(() => {}), conversationRefreshMs)
    }
  }

  function close(): void {
    const connection = useConnectionStore()
    for (const stream of live) void connection.unsubscribe(stream)
    live.clear()
    clearTimeout(refresh)
  }

  return { id, summary, events, hasMore, conversation, details, error, open, loadMore, loadConversation, append, close }
})
