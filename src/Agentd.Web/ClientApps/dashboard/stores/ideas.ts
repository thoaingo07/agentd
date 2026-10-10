import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, get, send } from '../../shared/api/http'
import type { AgentEvent, IdeaDetail, IdeaSummary } from '../../shared/api/types'

/** Debounce for re-reading after idea events (a reply and a status change arrive together). */
export const ideaRefreshDelayMs = 300
/** While the agent is writing a reply, the open idea is re-read this often (its reply also arrives as an event). */
export const thinkingPollMs = 3000
/** The choices once work items are proposed: sent as messages, exactly like chat's buttons. */
export const ideaChoices = { create: '✅ Create', start: '🚀 Create and start', change: '✏️ Change', discard: '🗑 Discard' } as const
export const ideaModels = ['fable', 'opus', 'sonnet'] as const
export const ideaEfforts = ['low', 'medium', 'high', 'xhigh', 'max'] as const

/**
 * Brainstormed ideas: started and talked through here or in chat (`!idea`). Kept fresh from the "all" stream: an
 * `idea.message` / `idea.updated` event (ids only) re-reads the list and the open idea.
 */
export const useIdeasStore = defineStore('ideas', () => {
  const list = ref<IdeaSummary[] | null>(null)
  const current = ref<IdeaDetail | null>(null)
  const currentId = ref<number | null>(null)
  const missing = ref(false)
  let timer: ReturnType<typeof setTimeout> | undefined
  let thinking: ReturnType<typeof setTimeout> | undefined

  async function load(): Promise<void> {
    list.value = await get<IdeaSummary[]>('/api/ideas')
  }

  async function open(id: number): Promise<void> {
    if (currentId.value !== id) current.value = null
    currentId.value = id
    missing.value = false
    try {
      const idea = await get<IdeaDetail>(`/api/ideas/${id}`)
      if (currentId.value !== id) return
      current.value = idea
      clearTimeout(thinking)
      if (idea.thinking) thinking = setTimeout(() => void open(id).catch(() => {}), thinkingPollMs)
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) missing.value = true
      else throw err
    }
  }

  /** Starts an idea on the Web UI (no chat thread); returns its id. */
  async function start(input: { text: string; repo?: string | null; model?: string | null; effort?: string | null }): Promise<number> {
    return (await send<{ id: number }>('POST', '/api/ideas', input)).id
  }

  /** A message to the open idea, or one of {@link ideaChoices}; handled like one in its chat thread. */
  async function message(text: string): Promise<void> {
    const id = currentId.value
    if (id === null) return
    await send('POST', `/api/ideas/${id}/messages`, { text })
    await open(id)
  }

  /** Model and effort from the agent's next reply. */
  async function settings(model: string | null, effort: string | null): Promise<void> {
    const id = currentId.value
    if (id === null) return
    await send('PUT', `/api/ideas/${id}/settings`, { model, effort })
    await open(id)
  }

  function close(): void {
    clearTimeout(thinking)
    currentId.value = null
    current.value = null
  }

  function apply(evt: AgentEvent): void {
    if (!evt.type.startsWith('idea.') || timer) return
    const id = Number((evt.payload as Record<string, unknown>).ideaId)
    timer = setTimeout(() => {
      timer = undefined
      if (list.value) void load().catch(() => {})
      if (currentId.value === id) void open(id).catch(() => {})
    }, ideaRefreshDelayMs)
  }

  return { list, current, currentId, missing, load, open, start, message, settings, close, apply }
})
