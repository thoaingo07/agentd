import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, get } from '../../shared/api/http'
import type { AgentEvent, IdeaDetail, IdeaSummary } from '../../shared/api/types'

/** Debounce for re-reading after idea events (a reply and a status change arrive together). */
export const ideaRefreshDelayMs = 300

/**
 * Brainstormed ideas (Phase 2d), read-only: they start and continue in chat with `!idea`. Kept fresh from the
 * "all" stream: an `idea.message` / `idea.updated` event (ids only) re-reads the list and the open idea.
 */
export const useIdeasStore = defineStore('ideas', () => {
  const list = ref<IdeaSummary[] | null>(null)
  const current = ref<IdeaDetail | null>(null)
  const currentId = ref<number | null>(null)
  const missing = ref(false)
  let timer: ReturnType<typeof setTimeout> | undefined

  async function load(): Promise<void> {
    list.value = await get<IdeaSummary[]>('/api/ideas')
  }

  async function open(id: number): Promise<void> {
    if (currentId.value !== id) current.value = null
    currentId.value = id
    missing.value = false
    try {
      const idea = await get<IdeaDetail>(`/api/ideas/${id}`)
      if (currentId.value === id) current.value = idea
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) missing.value = true
      else throw err
    }
  }

  function close(): void {
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

  return { list, current, currentId, missing, load, open, close, apply }
})
